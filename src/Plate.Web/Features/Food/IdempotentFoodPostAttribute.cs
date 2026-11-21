using Plate.Web.Data;
using Plate.Web.Features.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace Plate.Web.Features.Food;

[AttributeUsage(AttributeTargets.Method)]
public sealed class IdempotentFoodPostAttribute : TypeFilterAttribute
{
    public IdempotentFoodPostAttribute(bool review = false) : base(typeof(IdempotentFoodPostFilter)) =>
        Arguments = [review];
}

/// <summary>Claims a rendered food form and commits its receipt and food writes together.</summary>
public sealed class IdempotentFoodPostFilter(bool review, AppDbContext db, ICurrentUser currentUser, TimeProvider time) : IAsyncActionFilter
{
    public const string FieldName = "requestId";

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var ct = context.HttpContext.RequestAborted;
        if (!context.HttpContext.Request.HasFormContentType)
        {
            context.Result = new UnsupportedMediaTypeResult();
            return;
        }
        var form = await context.HttpContext.Request.ReadFormAsync(ct);
        if (form[FieldName].Count != 1 || !Guid.TryParse(form[FieldName], out var requestId) || requestId == Guid.Empty)
        {
            throw new ValidationException("This food form is out of date. Check whether it was saved, then reload before trying again.");
        }

        var userId = currentUser.UserId ?? throw new InvalidOperationException("No signed-in user.");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var createdAt = time.GetUtcNow();
        // A concurrent resend waits for the first transaction. If it rolls back, this caller can claim the intent.
        var claimed = await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "FoodLogRequests" ("UserId", "RequestId", "CreatedAt", "Location")
            VALUES ({userId}, {requestId}, {createdAt}, '')
            ON CONFLICT ("UserId", "RequestId") DO NOTHING
            """, ct);
        if (claimed == 0)
        {
            var previous = await db.FoodLogRequests.AsNoTracking().SingleAsync(x => x.RequestId == requestId, ct);
            if (review) ((Controller)context.Controller).TempData.SavedReview(requestId, alreadySaved: true);
            context.Result = new RedirectResult(previous.Location);
            return;
        }

        var executed = await next();
        if (executed.Exception is null && executed.Result is RedirectResult redirect)
        {
            await db.FoodLogRequests.Where(x => x.RequestId == requestId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Location, redirect.Url), ct);
            await transaction.CommitAsync(ct);
            if (review) ((Controller)context.Controller).TempData.SavedReview(requestId, alreadySaved: false);
        }
        // Validation/other failures roll back the claim and all writes, so the form can be corrected and retried.
    }
}

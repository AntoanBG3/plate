using System.Globalization;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace GymTracker.Web.Features;

/// <summary>
/// Numbers are shown with a dot in both languages (SPEC 8), but the Bulgarian iPhone keypad types a comma, so a decimal
/// is read with either. Empty is null for decimal?; anything else unreadable is a model error, as before.
/// </summary>
public sealed class DecimalModelBinder : IModelBinder
{
    public Task BindModelAsync(ModelBindingContext context)
    {
        var value = context.ValueProvider.GetValue(context.ModelName);
        if (value == ValueProviderResult.None)
        {
            return Task.CompletedTask;
        }

        context.ModelState.SetModelValue(context.ModelName, value);
        var text = value.FirstValue?.Trim() ?? "";
        if (text.Length == 0)
        {
            if (Nullable.GetUnderlyingType(context.ModelType) is not null)
            {
                context.Result = ModelBindingResult.Success(null);
            }

            return Task.CompletedTask;
        }

        if (decimal.TryParse(text.Replace(',', '.'), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
        {
            context.Result = ModelBindingResult.Success(number);
        }
        else
        {
            context.ModelState.TryAddModelError(context.ModelName, $"'{text}' is not a number.");
        }

        return Task.CompletedTask;
    }
}

public sealed class DecimalModelBinderProvider : IModelBinderProvider
{
    private static readonly DecimalModelBinder Binder = new();

    public IModelBinder? GetBinder(ModelBinderProviderContext context) =>
        context.Metadata.ModelType == typeof(decimal) || context.Metadata.ModelType == typeof(decimal?) ? Binder : null;
}

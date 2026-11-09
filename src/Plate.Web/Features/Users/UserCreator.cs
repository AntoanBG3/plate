using Plate.Core.Domain;
using Plate.Web.Data;
using Microsoft.AspNetCore.Identity;

namespace Plate.Web.Features.Users;

public class UserCreator(UserManager<AppUser> users, AppDbContext db)
{
    /// <summary>Creates a login and its profile. Returns the error messages, or an empty list on success.</summary>
    public async Task<IReadOnlyList<string>> CreateAsync(string email, string displayName, string password, CancellationToken ct) =>
        (await CreateUserAsync(email, displayName, password, Language.English, ct)).Errors;

    /// <summary>
    /// As <see cref="CreateAsync"/>, also returning the new login and setting the profile's language. The errors are
    /// English sentences the join page translates.
    /// </summary>
    public async Task<UserCreation> CreateUserAsync(string email, string displayName, string password, Language language, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Trim().Length > 100)
        {
            return new(null, [NameLength]);
        }

        var user = new AppUser { UserName = email.Trim(), Email = email.Trim(), EmailConfirmed = true };
        var result = await users.CreateAsync(user, password);
        if (!result.Succeeded)
        {
            // A taken email fails as both DuplicateUserName and DuplicateEmail, so one sentence covers both.
            return new(null, result.Errors.Select(Message).Distinct().ToList());
        }

        db.Profiles.Add(new UserProfile { UserId = user.Id, DisplayName = displayName.Trim(), Language = language });
        await db.SaveChangesAsync(ct);
        return new(user, []);
    }

    private static string NameLength => "Name must be 1 to 100 characters.";

    // Identity's own descriptions are English only; the errors a new account can hit get our sentences.
    private static string Message(IdentityError error) => error.Code switch
    {
        "DuplicateUserName" or "DuplicateEmail" => "An account with this email already exists.",
        "InvalidUserName" or "InvalidEmail" => "Enter a valid email address.",
        "PasswordTooShort" => "The password needs at least 12 characters.",
        _ => error.Description,
    };
}

public sealed record UserCreation(AppUser? User, IReadOnlyList<string> Errors)
{
    public bool Succeeded => User is not null;
}

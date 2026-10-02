using GymTracker.Core.Domain;
using GymTracker.Web.Data;
using Microsoft.AspNetCore.Identity;

namespace GymTracker.Web.Features.Users;

public class UserCreator(UserManager<AppUser> users, AppDbContext db)
{
    /// <summary>Creates a login and its profile. Returns Identity's error messages, or an empty list on success.</summary>
    public async Task<IReadOnlyList<string>> CreateAsync(string email, string displayName, string password, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Trim().Length > 100)
        {
            return ["Name must be 1 to 100 characters."];
        }

        var user = new AppUser { UserName = email.Trim(), Email = email.Trim(), EmailConfirmed = true };
        var result = await users.CreateAsync(user, password);
        if (!result.Succeeded)
        {
            return result.Errors.Select(e => e.Description).ToList();
        }

        db.Profiles.Add(new UserProfile { UserId = user.Id, DisplayName = displayName.Trim() });
        await db.SaveChangesAsync(ct);
        return [];
    }
}

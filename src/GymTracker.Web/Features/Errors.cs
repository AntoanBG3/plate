using GymTracker.Core.Text;

namespace GymTracker.Web.Features;

/// <summary>The entity doesn't exist or belongs to another user. Mapped to 404 either way.</summary>
public class NotFoundException(string message = "Not found.") : Exception(message);

/// <summary>Invalid input from the user. Mapped to 400 with the message shown in the user's language.</summary>
public class ValidationException(LocalText text) : Exception(text.ToString())
{
    public LocalText Text { get; } = text;

    public ValidationException(string message) : this(new LocalText(message))
    {
    }
}

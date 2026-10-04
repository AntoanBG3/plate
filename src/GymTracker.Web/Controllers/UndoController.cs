using GymTracker.Web.Features;
using GymTracker.Web.Features.Undo;
using Microsoft.AspNetCore.Mvc;

namespace GymTracker.Web.Controllers;

[Route("undo")]
public class UndoController(UndoService undo) : Controller
{
    private static readonly Dictionary<string, UndoKind> Kinds = new()
    {
        ["entry"] = UndoKind.Entry,
        ["set"] = UndoKind.Set,
        ["session-exercise"] = UndoKind.SessionExercise,
        ["template-exercise"] = UndoKind.TemplateExercise,
    };

    [HttpPost("{kind}/{id:int}")]
    public async Task<IActionResult> Restore(string kind, int id, string? returnUrl, CancellationToken ct)
    {
        if (!Kinds.TryGetValue(kind, out var k))
        {
            return NotFound();
        }

        await undo.RestoreAsync(k, id, ct);
        return Redirect(Url.SafeReturnUrl(returnUrl));
    }
}

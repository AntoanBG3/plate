namespace GymTracker.Core.Training;

public record TemplateOrder(int TemplateId, int SortOrder);

public static class NextWorkout
{
    /// <summary>
    /// The template after the last completed one, in sort order, wrapping around.
    /// With no completed template (or one that is no longer in the list), the first template.
    /// </summary>
    public static int? Pick(IReadOnlyList<TemplateOrder> templates, int? lastCompletedTemplateId)
    {
        if (templates.Count == 0)
        {
            return null;
        }

        var ordered = templates.OrderBy(t => t.SortOrder).ThenBy(t => t.TemplateId).ToList();
        var index = lastCompletedTemplateId is null
            ? -1
            : ordered.FindIndex(t => t.TemplateId == lastCompletedTemplateId);

        return ordered[(index + 1) % ordered.Count].TemplateId;
    }
}

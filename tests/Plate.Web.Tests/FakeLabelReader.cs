using Plate.Core.Nutrition;

namespace Plate.Web.Tests;

/// <summary>Stands in for the label model: answers with <see cref="Next"/> and counts the calls.</summary>
public sealed class FakeLabelReader : ILabelReader
{
    public static readonly LabelReading Yogurt = new(true, "Кисело мляко 3.6%", "Верея", LabelBasis.Per100g, 400, 62, 259, 3.2m, 4.1m, 3.6m, 2.3m, null);

    public LabelReadResult Next { get; set; } = new(Yogurt, "{}", null, "test/label");
    public bool Available { get; set; } = true;
    public int Calls { get; private set; }
    public IReadOnlyList<LabelImage>? LastImages { get; private set; }

    public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(Available);

    public Task<LabelReadResult> ReadAsync(IReadOnlyList<LabelImage> images, CancellationToken ct)
    {
        // Like the real HTTP call: a cancelled request never gets an answer.
        ct.ThrowIfCancellationRequested();
        Calls++;
        LastImages = images;
        return Task.FromResult(Next);
    }
}

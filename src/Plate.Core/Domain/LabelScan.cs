namespace Plate.Core.Domain;

/// <summary>One label photo read by the label model (SPEC 6.6), kept to measure misreads.</summary>
public class LabelScan
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string Model { get; set; } = "";
    public int LatencyMs { get; set; }
    public string? Error { get; set; }

    /// <summary>JSON: the model's answer as returned.</summary>
    public string? Reading { get; set; }

    /// <summary>JSON: the normalized LabelValues offered in the form.</summary>
    public string? Values { get; set; }

    public int? FoodItemId { get; set; }

    /// <summary>JSON: what the user saved; null until saved.</summary>
    public string? Saved { get; set; }

    public bool Edited { get; set; }
    public List<LabelScanPhoto> Photos { get; set; } = [];
}

/// <summary>One of a scan's photos (up to three, in the order taken), in its own table so lists never load them. Deleted after 30 days.</summary>
public class LabelScanPhoto
{
    public const int MaxBytes = 2 * 1024 * 1024;

    /// <summary>A label read takes up to three photos of the same package (SPEC 6.6).</summary>
    public const int MaxPerScan = 3;

    public int Id { get; set; }
    public int LabelScanId { get; set; }
    public LabelScan LabelScan { get; set; } = null!;

    /// <summary>0-based order in which the photos were taken.</summary>
    public int Position { get; set; }

    public string ContentType { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public byte[] Bytes { get; set; } = [];
}

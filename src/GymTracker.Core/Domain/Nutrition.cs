namespace GymTracker.Core.Domain;

public enum FoodSource
{
    OpenFoodFacts,
    Usda,
    Manual,
    Ai,
}

public enum Meal
{
    Breakfast,
    Lunch,
    Dinner,
    Snack,
}

public enum EntrySource
{
    Barcode,
    Search,
    Ai,
    Manual,
    Recent,
}

/// <summary>A reusable food definition. Shared (no owner) for the USDA subset, the Open Food Facts cache and manual barcode items.</summary>
public class FoodItem
{
    public int Id { get; set; }
    public string? OwnerUserId { get; set; }
    public string Name { get; set; } = "";
    public string? NameBg { get; set; }
    public string? Brand { get; set; }
    public string? Barcode { get; set; }
    public decimal KcalPer100g { get; set; }
    public decimal ProteinPer100g { get; set; }
    public decimal CarbsPer100g { get; set; }
    public decimal FatPer100g { get; set; }
    public decimal? DefaultServingGrams { get; set; }
    public FoodSource Source { get; set; }
}

/// <summary>One logged food. Totals are stored so the entry stays the same if its FoodItem changes.</summary>
public class FoodLogEntry
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public DateOnly Date { get; set; }
    public Meal Meal { get; set; }
    public string Description { get; set; } = "";
    public decimal Grams { get; set; }
    public decimal Kcal { get; set; }
    public decimal ProteinG { get; set; }
    public decimal CarbsG { get; set; }
    public decimal FatG { get; set; }
    public int? FoodItemId { get; set; }
    public FoodItem? FoodItem { get; set; }
    public EntrySource Source { get; set; }
    public string? AiModel { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

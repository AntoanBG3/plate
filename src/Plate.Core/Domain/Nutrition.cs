namespace Plate.Core.Domain;

public enum FoodSource
{
    OpenFoodFacts,
    Usda,
    Manual,
    Ai,

    /// <summary>Bulgarian foods and dishes compiled from public tables (tools/foods), shared.</summary>
    Reference,

    /// <summary>A home dish the user built from its ingredients (SPEC 5.12): always private, never shared or scanned.</summary>
    Recipe,
}

/// <summary>What a food's amounts are counted in. Its per-100 values are per 100 of this unit (SPEC 4, 6.6).</summary>
public enum FoodUnit
{
    Grams,

    /// <summary>Read from a per-100 ml label (a drink, an oil): amounts are entered and shown in ml.</summary>
    Millilitres,
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

    /// <summary>Calories and macros typed directly, with no food and no grams.</summary>
    Quick,
}

/// <summary>A reusable food definition. Shared (no owner) for the USDA subset, the Open Food Facts cache and manual barcode items.</summary>
public class FoodItem
{
    public int Id { get; set; }
    public string? OwnerUserId { get; set; }

    /// <summary>
    /// Who created a manual item; only used to say a shared food came from someone else (SPEC 4). Kept when that
    /// account is deleted; null when the creator is unknown.
    /// </summary>
    public string? AddedByUserId { get; set; }

    public string Name { get; set; } = "";
    public string? NameBg { get; set; }
    public string? Brand { get; set; }
    public string? Barcode { get; set; }
    public decimal KcalPer100g { get; set; }
    public decimal ProteinPer100g { get; set; }
    public decimal CarbsPer100g { get; set; }
    public decimal FatPer100g { get; set; }

    /// <summary>Null when the source has no value; unknown is not zero.</summary>
    public decimal? FiberPer100g { get; set; }

    /// <summary>Null when the source has no value; unknown is not zero.</summary>
    public decimal? SaturatedFatPer100g { get; set; }

    public decimal? DefaultServingGrams { get; set; }

    /// <summary>
    /// One piece's weight, for a counted amount ("две закуски"). Null when the source doesn't give it: a serving can be
    /// several pieces, so <see cref="DefaultServingGrams"/> never stands in for it.
    /// </summary>
    public decimal? PieceGrams { get; set; }

    /// <summary>The whole pack's net weight, for a fraction of a pack ("половин кофичка"); null when unknown.</summary>
    public decimal? PackGrams { get; set; }

    /// <summary>The per-100 g values are an estimate (from a recipe), not a printed label or a composition table.</summary>
    public bool ValuesEstimated { get; set; }

    /// <summary>
    /// Grams unless the values came from a per-100 ml label. Then the per-100 values, the serving and every amount
    /// logged of this food are in ml; nothing converts between the two.
    /// </summary>
    public FoodUnit Unit { get; set; }

    public FoodSource Source { get; set; }

    /// <summary>
    /// Open Food Facts had no name, so <see cref="Name"/> is the brand and quantity; the first user to log it names it
    /// for everyone (feedback #5).
    /// </summary>
    public bool Unnamed { get; set; }
}

/// <summary>One logged food. Totals are stored so the entry stays the same if its FoodItem changes.</summary>
public class FoodLogEntry
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public DateOnly Date { get; set; }
    public Meal Meal { get; set; }
    public string Description { get; set; } = "";

    /// <summary>The amount eaten, in <see cref="Unit"/> (the name is from when every amount was in grams).</summary>
    public decimal Grams { get; set; }

    /// <summary>The food's unit when it was logged, kept so a past day shows what it showed then.</summary>
    public FoodUnit Unit { get; set; }

    public decimal Kcal { get; set; }
    public decimal ProteinG { get; set; }
    public decimal CarbsG { get; set; }
    public decimal FatG { get; set; }

    /// <summary>Null when the food has no value; unknown is not zero.</summary>
    public decimal? FiberG { get; set; }

    /// <summary>Null when the food has no value; unknown is not zero.</summary>
    public decimal? SaturatedFatG { get; set; }

    public int? FoodItemId { get; set; }
    public FoodItem? FoodItem { get; set; }
    public EntrySource Source { get; set; }
    public string? AiModel { get; set; }

    /// <summary>The decision model version that chose the matched food, or decided there is none; null when the ranking did.</summary>
    public string? MatchModel { get; set; }

    /// <summary>The text parse this entry came from, so it can be reported later (SPEC 11).</summary>
    public int? ParseRunId { get; set; }

    /// <summary>
    /// How the food is, shown beside its name ("raw", "boiled", "dried"), in the language it was logged in. Set for parsed
    /// entries; for others the raw or cooked state is read from the linked food's name when shown.
    /// </summary>
    public string? Status { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Set when deleted; the row is hidden and can be restored for a while (undo), then purged.</summary>
    public DateTimeOffset? DeletedAt { get; set; }
}

/// <summary>Which food this account means by some typed words (SPEC 4, 6.1). Written on confirm, read before the ranking.</summary>
public class UsualFood
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";

    /// <summary>The 5.10 key of a food's name as the user typed it; unique per user.</summary>
    public string Phrase { get; set; } = "";
    public int FoodItemId { get; set; }
    public DateTimeOffset LastUsedAt { get; set; }
}

/// <summary>
/// A home dish built from its ingredients (SPEC 4, 5.12). Its values live on its <see cref="FoodItem"/>, a private
/// food with <see cref="FoodSource.Recipe"/> that is logged like any other; the food's owner owns the recipe.
/// Entries keep the totals they were logged with, so editing a recipe changes only later entries.
/// </summary>
public class Recipe
{
    public int FoodItemId { get; set; }
    public FoodItem FoodItem { get; set; } = null!;

    /// <summary>The finished dish's weight; null means the ingredients' raw weight is used.</summary>
    public decimal? YieldGrams { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
    public List<RecipeIngredient> Ingredients { get; set; } = [];
}

/// <summary>One ingredient as weighed into a recipe, with the per-100 g values used then (a snapshot, like a saved meal item).</summary>
public class RecipeIngredient
{
    public int Id { get; set; }
    public int RecipeFoodItemId { get; set; }
    public Recipe Recipe { get; set; } = null!;
    public int SortOrder { get; set; }
    public string Description { get; set; } = "";
    public decimal Grams { get; set; }

    /// <summary>The database food it was; null for a model's estimate, or once that food is deleted.</summary>
    public int? FoodItemId { get; set; }

    public decimal KcalPer100g { get; set; }
    public decimal ProteinPer100g { get; set; }
    public decimal CarbsPer100g { get; set; }
    public decimal FatPer100g { get; set; }
    public decimal? FiberPer100g { get; set; }
    public decimal? SaturatedFatPer100g { get; set; }
}

/// <summary>A named group of foods logged together often, copied from a logged meal.</summary>
public class SavedMeal
{
    public const int MaxNameLength = 60;

    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public List<SavedMealItem> Items { get; set; } = [];
}

/// <summary>A snapshot of one logged entry, with its stored totals, so later food edits don't change it.</summary>
public class SavedMealItem
{
    public int Id { get; set; }
    public int SavedMealId { get; set; }
    public SavedMeal SavedMeal { get; set; } = null!;
    public int SortOrder { get; set; }
    public string Description { get; set; } = "";

    /// <summary>0 for a quick entry, which has no amount. In <see cref="Unit"/>.</summary>
    public decimal Grams { get; set; }

    public FoodUnit Unit { get; set; }

    public decimal Kcal { get; set; }
    public decimal ProteinG { get; set; }
    public decimal CarbsG { get; set; }
    public decimal FatG { get; set; }
    public decimal? FiberG { get; set; }
    public decimal? SaturatedFatG { get; set; }
    public int? FoodItemId { get; set; }
    public EntrySource Source { get; set; }
}

public enum MaintenanceMethod
{
    /// <summary>Not enough logged data yet: the prior, or the last adaptive estimate carried forward.</summary>
    Initial,

    /// <summary>Calculated from logged intake and trend weight.</summary>
    Adaptive,
}

/// <summary>A weekly maintenance estimate and the targets it produced (SPEC 5.5).</summary>
public class MaintenanceEstimate
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public DateOnly CalculatedOn { get; set; }
    public int EstimatedTdeeKcal { get; set; }
    public int? TargetKcal { get; set; }
    public int? TargetProteinG { get; set; }
    public decimal? TrendWeightKg { get; set; }
    public int DaysOfData { get; set; }
    public decimal LoggingCompleteness { get; set; }
    public MaintenanceMethod Method { get; set; }

    /// <summary>The window had a gap of 3 or more days without a weigh-in.</summary>
    public bool LowConfidence { get; set; }
}

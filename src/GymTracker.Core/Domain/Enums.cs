namespace GymTracker.Core.Domain;

public enum Muscle
{
    Chest,
    Back,
    Shoulders,
    Biceps,
    Triceps,
    Forearms,
    Quads,
    Hamstrings,
    Glutes,
    Calves,
    Abs,
}

public enum Equipment
{
    Barbell,
    Dumbbell,
    Machine,
    Cable,
    Bodyweight,
    Other,
}

public enum Sex
{
    Male,
    Female,
}

/// <summary>NASEM 2023 physical activity categories (SPEC 5.5). The order matters: each is more active than the last.</summary>
public enum ActivityLevel
{
    Inactive,
    LowActive,
    Active,
    VeryActive,
}

public enum Goal
{
    Cut,
    Maintain,
    Bulk,
}

public enum Theme
{
    Dark,
    Light,
    System,
}

public enum Language
{
    English,
    Bulgarian,
}

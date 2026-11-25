namespace DoorNet.Core.Threats;

/// <summary>How a scored address is presented in the analysis list.</summary>
public enum UrlCategory
{
    Safe,
    Watch,
    Suspicious,
    Danger
}

public static class UrlCategories
{
    public static UrlCategory For(UrlAssessment assessment) =>
        assessment.IsExempt ? UrlCategory.Safe : For(assessment.Score);

    public static UrlCategory For(int score) => score switch
    {
        >= 75 => UrlCategory.Danger,
        >= 50 => UrlCategory.Suspicious,
        >= 30 => UrlCategory.Watch,
        _ => UrlCategory.Safe
    };

    public static string Label(UrlCategory category) => category switch
    {
        UrlCategory.Danger => "Danger",
        UrlCategory.Suspicious => "Suspicious",
        UrlCategory.Watch => "Watch",
        _ => "Safe"
    };

    /// <summary>Sort order for the list: danger first, safe last.</summary>
    public static int Rank(UrlCategory category) => category switch
    {
        UrlCategory.Danger => 0,
        UrlCategory.Suspicious => 1,
        UrlCategory.Watch => 2,
        _ => 3
    };
}

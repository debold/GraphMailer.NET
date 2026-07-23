namespace GraphMailer.Service.Configuration;

/// <summary>
/// Operator preferences for the recommendation hints shown in the ConfigTool and at the end of
/// the periodic operations report. The hints themselves are computed
/// (<see cref="Services.Advisor.RecommendationEngine"/>); only the "don't show me this again"
/// decision is persisted.
/// </summary>
public sealed class RecommendationOptions
{
    public const string SectionName = "Recommendations";

    /// <summary>
    /// Stable ids (see <c>RecommendationIds</c>) the operator has hidden. A dismissal is sticky:
    /// it survives the underlying condition being fixed and reappearing, and is only cleared by
    /// restoring the hint in the ConfigTool. Unknown ids are ignored.
    /// </summary>
    public List<string> Dismissed { get; init; } = [];
}

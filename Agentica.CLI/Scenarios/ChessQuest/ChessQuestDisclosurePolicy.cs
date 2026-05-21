namespace Agentica.CLI.Scenarios.ChessQuest;

public sealed record ChessQuestDisclosurePolicy(
    ChessQuestMode Mode,
    bool IncludeSan,
    bool IncludeCurrentCheckStatus,
    bool IncludeHostCandidateConsequences,
    bool IncludeMaterialCounts,
    bool IncludeTacticalLabels,
    bool IncludeEngineEvaluation,
    bool IncludeHiddenObjectiveHints,
    bool AllowLineProjection,
    int MaxProjectedLinesPerTurn,
    int MaxProjectedPliesPerLine,
    bool IncludeProjectionCaptures,
    bool AllowAttackInspection,
    bool AllowCandidateInspection = false,
    int MaxCandidateInspectionsPerTurn = 0,
    bool RequireLegalMoveObservationForPlay = true)
{
    public bool EffectiveAllowCandidateInspection =>
        AllowCandidateInspection ||
        (Mode == ChessQuestMode.StrictRefereeThreatAware && AllowAttackInspection);

    public int EffectiveMaxCandidateInspectionsPerTurn =>
        EffectiveAllowCandidateInspection
            ? Math.Max(1, MaxCandidateInspectionsPerTurn == 0 ? 3 : MaxCandidateInspectionsPerTurn)
            : 0;

    public static ChessQuestDisclosurePolicy StrictRefereeHard { get; } = new(
        Mode: ChessQuestMode.StrictRefereeHard,
        IncludeSan: false,
        IncludeCurrentCheckStatus: false,
        IncludeHostCandidateConsequences: false,
        IncludeMaterialCounts: false,
        IncludeTacticalLabels: false,
        IncludeEngineEvaluation: false,
        IncludeHiddenObjectiveHints: false,
        AllowLineProjection: false,
        MaxProjectedLinesPerTurn: 0,
        MaxProjectedPliesPerLine: 0,
        IncludeProjectionCaptures: false,
        AllowAttackInspection: false,
        AllowCandidateInspection: false,
        MaxCandidateInspectionsPerTurn: 0,
        RequireLegalMoveObservationForPlay: true);

    public static ChessQuestDisclosurePolicy StrictRefereeProjected { get; } = new(
        Mode: ChessQuestMode.StrictRefereeProjected,
        IncludeSan: false,
        IncludeCurrentCheckStatus: false,
        IncludeHostCandidateConsequences: false,
        IncludeMaterialCounts: false,
        IncludeTacticalLabels: false,
        IncludeEngineEvaluation: false,
        IncludeHiddenObjectiveHints: false,
        AllowLineProjection: true,
        MaxProjectedLinesPerTurn: 3,
        MaxProjectedPliesPerLine: 4,
        IncludeProjectionCaptures: true,
        AllowAttackInspection: false,
        AllowCandidateInspection: false,
        MaxCandidateInspectionsPerTurn: 0,
        RequireLegalMoveObservationForPlay: true);

    public static ChessQuestDisclosurePolicy StrictRefereeThreatAware { get; } = new(
        Mode: ChessQuestMode.StrictRefereeThreatAware,
        IncludeSan: false,
        IncludeCurrentCheckStatus: true,
        IncludeHostCandidateConsequences: false,
        IncludeMaterialCounts: false,
        IncludeTacticalLabels: false,
        IncludeEngineEvaluation: false,
        IncludeHiddenObjectiveHints: false,
        AllowLineProjection: true,
        MaxProjectedLinesPerTurn: 4,
        MaxProjectedPliesPerLine: 6,
        IncludeProjectionCaptures: true,
        AllowAttackInspection: true,
        AllowCandidateInspection: true,
        MaxCandidateInspectionsPerTurn: 3,
        RequireLegalMoveObservationForPlay: true);

    public static ChessQuestDisclosurePolicy ActorProbe { get; } = new(
        Mode: ChessQuestMode.ActorProbe,
        IncludeSan: false,
        IncludeCurrentCheckStatus: true,
        IncludeHostCandidateConsequences: false,
        IncludeMaterialCounts: false,
        IncludeTacticalLabels: false,
        IncludeEngineEvaluation: false,
        IncludeHiddenObjectiveHints: false,
        AllowLineProjection: false,
        MaxProjectedLinesPerTurn: 0,
        MaxProjectedPliesPerLine: 0,
        IncludeProjectionCaptures: false,
        AllowAttackInspection: false,
        AllowCandidateInspection: false,
        MaxCandidateInspectionsPerTurn: 0,
        RequireLegalMoveObservationForPlay: false);
}

using System.Text.Json;

namespace StardewAgent.Protocol;

public sealed record TilePoint(int X, int Y);

public sealed record PixelPoint(float X, float Y);

public sealed record InteractionTile(
    TilePoint Tile,
    string Facing,
    bool Reachable,
    int? PathCost);

public sealed record InventoryStack(
    int Slot,
    string QualifiedItemId,
    string Name,
    string Type,
    int Quantity,
    int? ToolUpgradeLevel = null,
    int? Water = null,
    int? WaterCapacity = null);

public sealed record GameState(
    string SaveId,
    int Time,
    string Season,
    int Day,
    int Year,
    string Weather,
    string Location,
    bool WorldReady,
    bool Paused,
    string? ActiveMenu,
    bool EventActive);

public sealed record PlayerState(
    TilePoint Tile,
    PixelPoint Pixel,
    string Facing,
    float Energy,
    int MaxEnergy,
    int Health,
    int MaxHealth,
    int Money,
    bool Busy,
    bool UsingTool,
    int SelectedSlot);

public sealed record LocalGrid(
    TilePoint Origin,
    int Width,
    int Height,
    IReadOnlyList<string> Rows,
    IReadOnlyDictionary<string, string> Legend);

public sealed record Entity(
    string Id,
    string Revision,
    string Kind,
    string Location,
    TilePoint Tile,
    bool Reachable,
    int? PathCost,
    IReadOnlyList<InteractionTile> InteractionTiles,
    IReadOnlyDictionary<string, object?> Properties);

public sealed record ObservationSummary(
    int DryCrops,
    int HarvestableCrops,
    int EmptyTilledTiles,
    int ReachableWaterSources,
    int Containers,
    IReadOnlyDictionary<string, int> OmittedEntities);

public sealed record Observation(
    string SchemaVersion,
    string ObservationId,
    long WorldRevision,
    DateTimeOffset CapturedAtUtc,
    GameState Game,
    PlayerState Player,
    IReadOnlyList<InventoryStack> Inventory,
    LocalGrid LocalGrid,
    IReadOnlyList<Entity> Entities,
    ObservationSummary Summary,
    ExecutionSummary? PreviousExecution = null);

public sealed record StateChange(string Path, object? Before, object? After);
public sealed record RemovedEntity(string Id, string Revision);
public sealed record StateDiff(
    string BaseObservationId,
    IReadOnlyList<StateChange> Changed,
    IReadOnlyList<Entity> EntitiesAdded,
    IReadOnlyList<Entity> EntitiesUpdated,
    IReadOnlyList<RemovedEntity> EntitiesRemoved);

public sealed record PlanAction(
    string ActionId,
    string Action,
    JsonElement Args,
    IReadOnlyList<string>? ContinueOn = null);

public sealed record StopConditions(float EnergyBelow, int GameTimeAtOrAfter, int MaxFailures);

public sealed record ActionPlan(
    string SchemaVersion,
    string PlanId,
    string ExpectedObservationId,
    string Goal,
    IReadOnlyList<PlanAction> Actions,
    StopConditions StopConditions,
    bool RequestReplanAfter);

public sealed record PreconditionResult(string Name, bool Passed);
public sealed record TraceEntry(string State, long Tick, string? Detail = null);
public sealed record RecoverySuggestion(string Action, IReadOnlyDictionary<string, object?>? Args = null);

public sealed record ActionResult(
    string ActionId,
    string Action,
    string? TargetId,
    string Status,
    string Code,
    string Message,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset EndedAtUtc,
    int GameTimeBefore,
    int GameTimeAfter,
    int Attempts,
    IReadOnlyList<PreconditionResult> Preconditions,
    bool Retryable,
    bool StateChanged,
    StateDiff? StateDiff,
    IReadOnlyList<RecoverySuggestion> SuggestedRecovery,
    IReadOnlyList<TraceEntry> Trace);

public sealed record BudgetUsage(
    int Actions,
    int MovementTiles,
    int ToolUses,
    int PathReplans,
    int Failures,
    double WallClockSeconds);

public sealed record ExecutionResult(
    string ExecutionId,
    string PlanId,
    string Status,
    DateTimeOffset AcceptedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? EndedAtUtc,
    IReadOnlyList<ActionResult> Actions,
    BudgetUsage BudgetUsage,
    Observation? FinalObservation,
    bool RequiresReplan,
    string? Message = null);

public sealed record ExecutionSummary(
    string ExecutionId,
    string Status,
    string Summary,
    IReadOnlyList<ActionFailureSummary> Failures);

public sealed record ActionFailureSummary(string Action, string? Target, string Code);

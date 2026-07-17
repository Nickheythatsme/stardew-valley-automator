using System.Text.Json;
using Microsoft.Xna.Framework;
using StardewAgent.Core.Execution;
using StardewAgent.Mod.Game;
using StardewAgent.Mod.Navigation;
using StardewAgent.Mod.Observation;
using StardewAgent.Mod.Protocol;
using StardewAgent.Protocol;
using StardewModdingAPI;
using StardewValley;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;
using ProtocolObservation = StardewAgent.Protocol.Observation;

namespace StardewAgent.Mod.Execution;

internal sealed class PlanExecutor
{
    private readonly IGameFacade game;
    private readonly IModHelper helper;
    private readonly ObservationBuilder observations;
    private readonly ProtocolServer protocol;
    private readonly MovementController movement;
    private readonly Dictionary<string, MutableExecution> executions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> planIds = new(StringComparer.Ordinal);
    private MutableExecution? active;
    private ActiveAction? currentAction;
    private bool cancelRequested;

    public PlanExecutor(IGameFacade game, IModHelper helper, ObservationBuilder observations, ProtocolServer protocol)
    {
        this.game = game;
        this.helper = helper;
        this.observations = observations;
        this.protocol = protocol;
        movement = new(game);
    }

    public bool IsRunning => active is not null;
    public SButton? LastInjectedButton { get; private set; }

    public string Start(ActionPlan plan)
    {
        if (planIds.TryGetValue(plan.PlanId, out var existing))
            return existing;
        if (active is not null)
            throw new GameStateException("EXECUTION_IN_PROGRESS", "Only one plan may execute at a time.");
        var validation = PlanValidation.Validate(plan, observations.CurrentObservationId ?? string.Empty);
        if (!validation.Valid)
            throw new PlanRejectedException(validation);

        var executionId = $"exec-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        active = new MutableExecution(executionId, plan, now);
        executions[executionId] = active;
        planIds[plan.PlanId] = executionId;
        protocol.Publish("execution_state_changed", executionId, new { status = "ACCEPTED" });
        return executionId;
    }

    public ExecutionResult Get(string executionId)
    {
        if (!executions.TryGetValue(executionId, out var execution))
            throw new GameStateException("EXECUTION_NOT_FOUND", "The requested execution ID is unknown.");
        return execution.Snapshot();
    }

    public void Cancel(string executionId)
    {
        if (!executions.TryGetValue(executionId, out var execution))
            throw new GameStateException("EXECUTION_NOT_FOUND", "The requested execution ID is unknown.");
        if (ReferenceEquals(execution, active))
            cancelRequested = true;
    }

    public void CancelActive(string code, string message)
    {
        if (active is not null)
            FinishExecution(code, message, true);
    }

    public void ApplyInput()
    {
        LastInjectedButton = null;
        if (active is null || currentAction is null)
            return;
        if (movement.Running)
        {
            var button = movement.InputForCurrentTick();
            if (button is not null)
            {
                LastInjectedButton = button;
                helper.Input.Press(button.Value);
            }
            return;
        }
        if (currentAction.Phase == ActionPhase.TriggerTool && !currentAction.InputTriggered)
        {
            var button = Game1.options.useToolButton.Count() > 0
                ? Game1.options.useToolButton[0].ToSButton()
                : SButton.MouseLeft;
            LastInjectedButton = button;
            helper.Input.Press(button);
            currentAction.InputTriggered = true;
            currentAction.Phase = ActionPhase.WaitForTool;
            currentAction.Trace.Add(new("TRIGGER_GAME_ACTION", game.Tick));
        }
    }

    public void Tick()
    {
        LastInjectedButton = null;
        if (active is null)
            return;
        if (cancelRequested)
        {
            FinishExecution("CANCELLED", "Cancellation was requested.", true);
            return;
        }
        if (!game.WorldReady || !game.IsFarm)
        {
            FinishExecution("GAME_INTERRUPTED", "The game left the supported Farm state.", true);
            return;
        }
        var exceeded = active.Budget.Exceeded(DateTimeOffset.UtcNow);
        if (exceeded is not null)
        {
            FinishExecution("BUDGET_EXCEEDED", exceeded, true);
            return;
        }
        if (game.Player.Stamina < active.Plan.StopConditions.EnergyBelow || game.TimeOfDay >= active.Plan.StopConditions.GameTimeAtOrAfter)
        {
            FinishExecution("COMPLETED_WITH_FAILURES", "A plan stop condition was reached.", true);
            return;
        }
        if (active.Status == "ACCEPTED")
        {
            active.Status = "RUNNING";
            active.StartedAtUtc = DateTimeOffset.UtcNow;
            protocol.Publish("execution_state_changed", active.ExecutionId, new { status = active.Status });
        }
        if (currentAction is null)
        {
            if (active.ActionIndex >= active.Plan.Actions.Count)
            {
                FinishExecution(active.Results.Any(result => result.Status == "failed") ? "COMPLETED_WITH_FAILURES" : "COMPLETED", "The action queue completed.", false);
                return;
            }
            Begin(active.Plan.Actions[active.ActionIndex]);
        }
        TickCurrentAction();
    }

    private void Begin(PlanAction action)
    {
        if (active is null)
            return;
        active.Budget.RecordAction();
        currentAction = new ActiveAction(action, DateTimeOffset.UtcNow, game.TimeOfDay);
        currentAction.BaseObservationId = observations.CurrentObservationId ?? string.Empty;
        currentAction.Trace.Add(new("CHECK_PRECONDITIONS", game.Tick));
        switch (action.Action)
        {
            case "finish":
                Succeed("FINISHED", action.Args.GetProperty("reason").GetString() ?? "Finished.", false);
                FinishExecution("COMPLETED", "The plan finished explicitly.", false);
                break;
            case "wait":
                currentAction.RemainingTicks = action.Args.GetProperty("ticks").GetInt32();
                currentAction.Phase = ActionPhase.Wait;
                break;
            case "move_to":
                var tile = action.Args.GetProperty("tile");
                StartMovement(new TilePoint(tile[0].GetInt32(), tile[1].GetInt32()));
                break;
            case "water_crop":
                BeginWaterCrop();
                break;
            case "refill_watering_can":
                BeginRefillWateringCan();
                break;
            default:
                Fail("ACTION_NOT_IMPLEMENTED", $"Action '{action.Action}' is reserved by the schema but is not enabled in this prototype.", false);
                break;
        }
    }

    private void BeginWaterCrop()
    {
        if (currentAction is null || observations.LastObservation is null)
            return;
        var targetId = currentAction.PlanAction.Args.GetProperty("target_id").GetString()!;
        var targetRevision = currentAction.PlanAction.Args.GetProperty("target_revision").GetString()!;
        currentAction.TargetId = targetId;
        var entity = observations.LastObservation.Entities.FirstOrDefault(entity => entity.Id == targetId);
        currentAction.Preconditions.Add(new("target_exists", entity is not null));
        if (entity is null || entity.Kind != "crop")
        {
            Fail("TARGET_STALE", "The crop target no longer exists.", false);
            return;
        }
        currentAction.Preconditions.Add(new("target_revision_matches", entity.Revision == targetRevision));
        if (entity.Revision != targetRevision)
        {
            Fail("TARGET_STALE", "The crop revision changed.", false);
            return;
        }
        var dirt = game.GetDirt(new Point(entity.Tile.X, entity.Tile.Y));
        if (dirt is null || dirt.crop is null)
        {
            Fail("TARGET_STALE", "The crop target is no longer present.", false);
            return;
        }
        currentAction.Preconditions.Add(new("target_is_dry_crop", !dirt.isWatered() && dirt.needsWatering()));
        if (dirt.isWatered() || !dirt.needsWatering())
        {
            Fail("ALREADY_SATISFIED", "The crop does not need watering.", true);
            return;
        }
        var wateringCan = game.FindWateringCan(out var slot);
        currentAction.Preconditions.Add(new("watering_can_exists", wateringCan is not null));
        if (wateringCan is null)
        {
            Fail("NO_WATERING_CAN", "No watering can was found in inventory.", false);
            return;
        }
        currentAction.Preconditions.Add(new("watering_can_has_water", wateringCan.WaterLeft > 0));
        if (wateringCan.WaterLeft <= 0)
        {
            Fail("NO_WATER", "The watering can is empty.", true, new RecoverySuggestion("refill_watering_can"));
            return;
        }
        currentAction.ToolSlot = slot;
        currentAction.WaterBefore = wateringCan.WaterLeft;
        currentAction.TargetTile = entity.Tile;
        var interaction = entity.InteractionTiles
            .Where(tile => tile.Reachable)
            .OrderBy(tile => tile.PathCost ?? int.MaxValue)
            .FirstOrDefault();
        if (interaction is null)
        {
            Fail("TARGET_UNREACHABLE", "No reachable crop interaction tile exists.", true);
            return;
        }
        currentAction.Facing = interaction.Facing;
        StartMovement(interaction.Tile);
    }

    private void BeginRefillWateringCan()
    {
        if (currentAction is null || observations.LastObservation is null)
            return;
        var wateringCan = game.FindWateringCan(out var slot);
        currentAction.Preconditions.Add(new("watering_can_exists", wateringCan is not null));
        if (wateringCan is null)
        {
            Fail("NO_WATERING_CAN", "No watering can was found in inventory.", false);
            return;
        }
        currentAction.Preconditions.Add(new("watering_can_not_full", wateringCan.WaterLeft < wateringCan.waterCanMax));
        if (wateringCan.WaterLeft >= wateringCan.waterCanMax)
        {
            Fail("CAN_ALREADY_FULL", "The watering can is already full.", false);
            return;
        }

        var requestedSource = currentAction.PlanAction.Args.TryGetProperty("source_id", out var sourceProperty)
            ? sourceProperty.GetString()
            : null;
        var source = observations.LastObservation.Entities
            .Where(entity => entity.Kind == "water_source" && entity.Reachable)
            .Where(entity => requestedSource is null || entity.Id == requestedSource)
            .OrderBy(entity => entity.PathCost ?? int.MaxValue)
            .ThenBy(entity => entity.Id, StringComparer.Ordinal)
            .FirstOrDefault();
        currentAction.Preconditions.Add(new("water_source_exists", source is not null));
        if (source is null)
        {
            Fail(requestedSource is null ? "NO_WATER_SOURCE" : "TARGET_UNREACHABLE", "No reachable refill source was found.", true);
            return;
        }

        var interaction = source.InteractionTiles
            .Where(tile => tile.Reachable)
            .OrderBy(tile => tile.PathCost ?? int.MaxValue)
            .First();
        currentAction.TargetId = source.Id;
        currentAction.TargetTile = source.Tile;
        currentAction.ToolSlot = slot;
        currentAction.WaterBefore = wateringCan.WaterLeft;
        currentAction.Facing = interaction.Facing;
        StartMovement(interaction.Tile);
    }

    private void StartMovement(TilePoint target)
    {
        if (currentAction is null)
            return;
        currentAction.Trace.Add(new("MOVE_TO_INTERACTION_TILE", game.Tick, $"{target.X},{target.Y}"));
        if (!movement.Start(target))
        {
            Fail(movement.FailureCode ?? "NO_PATH", "No path to the target tile was found.", true);
            return;
        }
        currentAction.Phase = ActionPhase.Move;
    }

    private void TickCurrentAction()
    {
        if (currentAction is null || active is null)
            return;
        switch (currentAction.Phase)
        {
            case ActionPhase.Move:
                movement.Tick();
                if (movement.FailureCode is not null)
                {
                    Fail(movement.FailureCode, "Movement failed.", true);
                    return;
                }
                if (!movement.Completed)
                    return;
                for (var i = 0; i < movement.TraversedTiles; i++) active.Budget.RecordMovementTile();
                if (currentAction.PlanAction.Action == "move_to")
                {
                    Succeed("OK", "The target tile was reached.", true);
                    return;
                }
                PrepareWateringTool();
                break;
            case ActionPhase.Wait:
                currentAction.RemainingTicks--;
                if (currentAction.RemainingTicks <= 0)
                    Succeed("OK", "The requested wait completed.", false);
                break;
            case ActionPhase.WaitForTool:
                currentAction.ToolWaitTicks++;
                if (game.Player.UsingTool)
                    currentAction.SawToolUse = true;
                if (currentAction.PlanAction.Action == "refill_watering_can")
                {
                    var can = game.FindWateringCan(out _);
                    if (!game.Player.UsingTool && can is not null && can.WaterLeft > currentAction.WaterBefore)
                    {
                        Succeed("OK", "The watering can water level increased.", true);
                        return;
                    }
                    if (currentAction.ToolWaitTicks > 180)
                        Fail("REFILL_NOT_CONFIRMED", "Refilling was not verified before the timeout.", true);
                    return;
                }
                var dirt = currentAction.TargetTile is null
                    ? null
                    : game.GetDirt(new Point(currentAction.TargetTile.X, currentAction.TargetTile.Y));
                if (currentAction.SawToolUse && !game.Player.UsingTool && dirt?.isWatered() == true)
                {
                    Succeed("OK", "The crop changed from dry to watered.", true);
                    return;
                }
                if (currentAction.ToolWaitTicks > 180)
                    Fail("TOOL_USE_FAILED", "Watering was not verified before the timeout.", true);
                break;
        }
    }

    private void PrepareWateringTool()
    {
        if (currentAction is null || currentAction.ToolSlot is null)
            return;
        game.SelectTool(currentAction.ToolSlot.Value);
        game.Face(Direction(currentAction.Facing));
        currentAction.Trace.Add(new("SELECT_TOOL_OR_ITEM", game.Tick));
        currentAction.Trace.Add(new("FACE", game.Tick, currentAction.Facing));
        currentAction.Phase = ActionPhase.TriggerTool;
        active?.Budget.RecordToolUse();
    }

    private void Succeed(string code, string message, bool changed) => CompleteAction("succeeded", code, message, false, changed);

    private void Fail(string code, string message, bool retryable, params RecoverySuggestion[] recovery) =>
        CompleteAction("failed", code, message, retryable, false, recovery);

    private void CompleteAction(
        string status,
        string code,
        string message,
        bool retryable,
        bool stateChanged,
        params RecoverySuggestion[] recovery)
    {
        if (active is null || currentAction is null)
            return;
        currentAction.Trace.Add(new(status == "succeeded" ? "COMPLETED" : "FAILED", game.Tick, code));
        var result = new ActionResult(
            currentAction.PlanAction.ActionId,
            currentAction.PlanAction.Action,
            currentAction.TargetId,
            status,
            code,
            message,
            currentAction.StartedAtUtc,
            DateTimeOffset.UtcNow,
            currentAction.GameTimeBefore,
            game.TimeOfDay,
            1,
            currentAction.Preconditions,
            retryable,
            stateChanged,
            BuildStateDiff(currentAction, stateChanged),
            recovery,
            currentAction.Trace);
        active.Results.Add(result);
        protocol.Publish("action_completed", active.ExecutionId, result);
        if (status == "failed")
        {
            active.Budget.RecordFailure();
            var continueOn = currentAction.PlanAction.ContinueOn ?? Array.Empty<string>();
            if (!continueOn.Contains(code) || active.Budget.Failures > active.Plan.StopConditions.MaxFailures)
            {
                currentAction = null;
                FinishExecution("COMPLETED_WITH_FAILURES", message, true);
                return;
            }
        }
        active.ActionIndex++;
        currentAction = null;
    }

    private StateDiff? BuildStateDiff(ActiveAction action, bool changed)
    {
        if (!changed)
            return null;
        var changes = new List<StateChange>();
        if (Math.Abs(game.Player.Stamina - action.EnergyBefore) > 0.001f)
            changes.Add(new("/player/energy", action.EnergyBefore, game.Player.Stamina));
        if (action.ToolSlot is not null && game.FindWateringCan(out var slot) is { } can && slot == action.ToolSlot)
        {
            if (action.WaterBefore != can.WaterLeft)
                changes.Add(new($"/inventory/{slot}/water", action.WaterBefore, can.WaterLeft));
        }
        if (action.PlanAction.Action == "water_crop" && action.TargetId is not null)
            changes.Add(new($"/entities_by_id/{EscapePointer(action.TargetId)}/properties/watered", false, true));
        return new StateDiff(
            action.BaseObservationId,
            changes,
            Array.Empty<Entity>(),
            Array.Empty<Entity>(),
            Array.Empty<RemovedEntity>());
    }

    private static string EscapePointer(string value) => value.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    private void FinishExecution(string status, string message, bool requiresReplan)
    {
        if (active is null)
            return;
        movement.Cancel(status);
        active.Status = status;
        active.Message = message;
        active.RequiresReplan = requiresReplan;
        active.EndedAtUtc = DateTimeOffset.UtcNow;
        try
        {
            active.FinalObservation = observations.Build(previousExecution: active.Summary());
        }
        catch (GameStateException)
        {
            active.FinalObservation = null;
        }
        protocol.Publish("execution_state_changed", active.ExecutionId, new { status, message });
        active = null;
        currentAction = null;
        cancelRequested = false;
    }

    private static int Direction(string? facing) => facing switch
    {
        "up" => Game1.up, "right" => Game1.right, "down" => Game1.down, "left" => Game1.left, _ => Game1.down
    };

    private sealed class MutableExecution
    {
        public MutableExecution(string executionId, ActionPlan plan, DateTimeOffset acceptedAtUtc)
        {
            ExecutionId = executionId;
            Plan = plan;
            AcceptedAtUtc = acceptedAtUtc;
        }

        public string ExecutionId { get; }
        public ActionPlan Plan { get; }
        public DateTimeOffset AcceptedAtUtc { get; }
        public DateTimeOffset? StartedAtUtc { get; set; }
        public DateTimeOffset? EndedAtUtc { get; set; }
        public string Status { get; set; } = "ACCEPTED";
        public string? Message { get; set; }
        public int ActionIndex { get; set; }
        public List<ActionResult> Results { get; } = new();
        public ExecutionBudget Budget { get; } = new();
        public ProtocolObservation? FinalObservation { get; set; }
        public bool RequiresReplan { get; set; }

        public ExecutionResult Snapshot() => new(
            ExecutionId, Plan.PlanId, Status, AcceptedAtUtc, StartedAtUtc, EndedAtUtc,
            Results.ToArray(), Budget.Snapshot(DateTimeOffset.UtcNow), FinalObservation, RequiresReplan, Message);

        public ExecutionSummary Summary() => new(
            ExecutionId,
            Status,
            Message ?? Status,
            Results.Where(result => result.Status == "failed")
                .Select(result => new ActionFailureSummary(result.Action, result.TargetId, result.Code)).ToArray());
    }

    private sealed class ActiveAction
    {
        public ActiveAction(PlanAction planAction, DateTimeOffset startedAtUtc, int gameTimeBefore)
        {
            PlanAction = planAction;
            StartedAtUtc = startedAtUtc;
            GameTimeBefore = gameTimeBefore;
            EnergyBefore = Game1.player.Stamina;
            BaseObservationId = string.Empty;
        }

        public PlanAction PlanAction { get; }
        public DateTimeOffset StartedAtUtc { get; }
        public int GameTimeBefore { get; }
        public float EnergyBefore { get; }
        public string BaseObservationId { get; set; }
        public ActionPhase Phase { get; set; }
        public string? TargetId { get; set; }
        public TilePoint? TargetTile { get; set; }
        public int? ToolSlot { get; set; }
        public string? Facing { get; set; }
        public int RemainingTicks { get; set; }
        public int ToolWaitTicks { get; set; }
        public int WaterBefore { get; set; }
        public bool InputTriggered { get; set; }
        public bool SawToolUse { get; set; }
        public List<PreconditionResult> Preconditions { get; } = new();
        public List<TraceEntry> Trace { get; } = new();
    }

    private enum ActionPhase { None, Move, Wait, TriggerTool, WaitForTool }
}

internal sealed class PlanRejectedException : Exception
{
    public PlanRejectedException(ValidationResult validation) : base("The action plan failed validation.") => Validation = validation;
    public ValidationResult Validation { get; }
}

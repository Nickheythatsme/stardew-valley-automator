using StardewAgent.Protocol;

namespace StardewAgent.Core.Execution;

public sealed class ExecutionBudget
{
    private readonly DateTimeOffset startedAt;

    public ExecutionBudget(DateTimeOffset? startedAt = null) => this.startedAt = startedAt ?? DateTimeOffset.UtcNow;

    public int Actions { get; private set; }
    public int MovementTiles { get; private set; }
    public int ToolUses { get; private set; }
    public int PathReplans { get; private set; }
    public int Failures { get; private set; }

    public void RecordAction() => Actions++;
    public void RecordMovementTile() => MovementTiles++;
    public void RecordToolUse() => ToolUses++;
    public void RecordPathReplan() => PathReplans++;
    public void RecordFailure() => Failures++;

    public string? Exceeded(DateTimeOffset now)
    {
        if (now - startedAt > ProtocolLimits.MaxExecutionDuration) return "WALL_CLOCK_BUDGET_EXCEEDED";
        if (Actions > ProtocolLimits.MaxActions) return "ACTION_BUDGET_EXCEEDED";
        if (MovementTiles > ProtocolLimits.MaxMovementTiles) return "MOVEMENT_BUDGET_EXCEEDED";
        if (ToolUses > ProtocolLimits.MaxToolUses) return "TOOL_USE_BUDGET_EXCEEDED";
        if (PathReplans > ProtocolLimits.MaxPathReplans) return "PATH_REPLAN_BUDGET_EXCEEDED";
        return null;
    }

    public BudgetUsage Snapshot(DateTimeOffset now) => new(
        Actions, MovementTiles, ToolUses, PathReplans, Failures, (now - startedAt).TotalSeconds);
}

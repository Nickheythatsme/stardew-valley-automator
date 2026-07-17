namespace StardewAgent.Core.Execution;

public enum ExecutionState
{
    Received,
    Parsed,
    Validated,
    Accepted,
    Running,
    WaitingForGame,
    Completed,
    CompletedWithFailures,
    Cancelled,
    TimedOut,
    BudgetExceeded,
    ValidationFailed,
    GameInterrupted,
    FatalError
}

public static class ExecutionStateMachine
{
    private static readonly IReadOnlyDictionary<ExecutionState, HashSet<ExecutionState>> Allowed =
        new Dictionary<ExecutionState, HashSet<ExecutionState>>
        {
            [ExecutionState.Received] = new() { ExecutionState.Parsed, ExecutionState.ValidationFailed },
            [ExecutionState.Parsed] = new() { ExecutionState.Validated, ExecutionState.ValidationFailed },
            [ExecutionState.Validated] = new() { ExecutionState.Accepted, ExecutionState.ValidationFailed },
            [ExecutionState.Accepted] = new() { ExecutionState.Running, ExecutionState.Cancelled },
            [ExecutionState.Running] = new()
            {
                ExecutionState.WaitingForGame, ExecutionState.Completed, ExecutionState.CompletedWithFailures,
                ExecutionState.Cancelled, ExecutionState.TimedOut, ExecutionState.BudgetExceeded,
                ExecutionState.GameInterrupted, ExecutionState.FatalError
            },
            [ExecutionState.WaitingForGame] = new()
            {
                ExecutionState.Running, ExecutionState.Cancelled, ExecutionState.TimedOut,
                ExecutionState.BudgetExceeded, ExecutionState.GameInterrupted, ExecutionState.FatalError
            }
        };

    public static bool IsTerminal(ExecutionState state) => state is
        ExecutionState.Completed or ExecutionState.CompletedWithFailures or ExecutionState.Cancelled or
        ExecutionState.TimedOut or ExecutionState.BudgetExceeded or ExecutionState.ValidationFailed or
        ExecutionState.GameInterrupted or ExecutionState.FatalError;

    public static void EnsureTransition(ExecutionState from, ExecutionState to)
    {
        if (!Allowed.TryGetValue(from, out var targets) || !targets.Contains(to))
            throw new InvalidOperationException($"Invalid execution transition: {from} -> {to}.");
    }
}

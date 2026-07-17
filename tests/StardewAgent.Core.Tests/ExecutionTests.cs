using StardewAgent.Core.Execution;
using Xunit;

namespace StardewAgent.Core.Tests;

public sealed class ExecutionTests
{
    [Fact]
    public void AllowsExpectedLifecycle()
    {
        ExecutionStateMachine.EnsureTransition(ExecutionState.Received, ExecutionState.Parsed);
        ExecutionStateMachine.EnsureTransition(ExecutionState.Parsed, ExecutionState.Validated);
        ExecutionStateMachine.EnsureTransition(ExecutionState.Validated, ExecutionState.Accepted);
        ExecutionStateMachine.EnsureTransition(ExecutionState.Accepted, ExecutionState.Running);
        ExecutionStateMachine.EnsureTransition(ExecutionState.Running, ExecutionState.Completed);
        Assert.True(ExecutionStateMachine.IsTerminal(ExecutionState.Completed));
    }

    [Fact]
    public void RejectsInvalidLifecycle()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ExecutionStateMachine.EnsureTransition(ExecutionState.Received, ExecutionState.Completed));
    }
}

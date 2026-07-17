using System.Text.Json;
using StardewAgent.Protocol;
using Xunit;

namespace StardewAgent.Protocol.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public void SerializesSnakeCaseEnvelope()
    {
        var envelope = ResponseEnvelope.Success("req-1", new { SchemaVersion = "2.0" });
        var json = JsonSerializer.Serialize(envelope, JsonDefaults.Options);
        Assert.Contains("\"protocol_version\":\"1.0\"", json);
        Assert.Contains("\"schema_version\":\"2.0\"", json);
    }

    [Fact]
    public void RejectsStalePlan()
    {
        using var arguments = JsonDocument.Parse("{\"reason\":\"done\"}");
        var plan = new ActionPlan(
            "2.0",
            "plan-1",
            "obs-old",
            "test",
            new[] { new PlanAction("a1", "finish", arguments.RootElement.Clone()) },
            new StopConditions(25, 1200, 1),
            false);

        var result = PlanValidation.Validate(plan, "obs-current");

        Assert.False(result.Valid);
        Assert.Contains(result.Issues, issue => issue.Code == "STALE_OBSERVATION");
    }
}

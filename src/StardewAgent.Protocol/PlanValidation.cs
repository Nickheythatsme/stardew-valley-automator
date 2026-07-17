using System.Text.Json;

namespace StardewAgent.Protocol;

public sealed record ValidationIssue(string Path, string Code, string Message);
public sealed record ValidationResult(bool Valid, IReadOnlyList<ValidationIssue> Issues);

public static class PlanValidation
{
    private static readonly HashSet<string> AllowedActions = new(StringComparer.Ordinal)
    {
        "move_to", "water_crop", "refill_watering_can", "harvest_crop",
        "plant_seed", "deposit_items", "wait", "finish"
    };

    private static readonly HashSet<string> AllowedContinueCodes = new(StringComparer.Ordinal)
    {
        "ALREADY_SATISFIED", "TARGET_UNREACHABLE"
    };

    public static ValidationResult Validate(ActionPlan plan, string currentObservationId)
    {
        var issues = new List<ValidationIssue>();
        if (plan.SchemaVersion != ProtocolLimits.SchemaVersion)
            issues.Add(new("/schema_version", "UNSUPPORTED_SCHEMA", "Unsupported action-plan schema version."));
        if (string.IsNullOrWhiteSpace(plan.PlanId) || plan.PlanId.Length > 128)
            issues.Add(new("/plan_id", "INVALID_PLAN_ID", "plan_id must contain 1 to 128 characters."));
        if (!string.Equals(plan.ExpectedObservationId, currentObservationId, StringComparison.Ordinal))
            issues.Add(new("/expected_observation_id", "STALE_OBSERVATION", "The plan does not target the current observation."));
        if (string.IsNullOrWhiteSpace(plan.Goal) || plan.Goal.Length > 512)
            issues.Add(new("/goal", "INVALID_GOAL", "goal must contain 1 to 512 characters."));
        if (plan.Actions.Count is < 1 or > ProtocolLimits.MaxActions)
            issues.Add(new("/actions", "INVALID_ACTION_COUNT", $"A plan must contain 1 to {ProtocolLimits.MaxActions} actions."));

        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < plan.Actions.Count; i++)
        {
            var action = plan.Actions[i];
            var path = $"/actions/{i}";
            if (string.IsNullOrWhiteSpace(action.ActionId) || !ids.Add(action.ActionId))
                issues.Add(new($"{path}/action_id", "DUPLICATE_ACTION_ID", "Action IDs must be non-empty and unique."));
            if (!AllowedActions.Contains(action.Action))
                issues.Add(new($"{path}/action", "UNSUPPORTED_ACTION", $"Action '{action.Action}' is not allowlisted."));
            if (action.Args.ValueKind != JsonValueKind.Object)
                issues.Add(new($"{path}/args", "INVALID_ARGUMENTS", "args must be a JSON object."));
            if (action.ContinueOn is not null && action.ContinueOn.Any(code => !AllowedContinueCodes.Contains(code)))
                issues.Add(new($"{path}/continue_on", "INVALID_CONTINUE_CODE", "continue_on contains a disallowed failure code."));
            ValidateActionArgs(action, path, issues);
        }

        if (plan.StopConditions.EnergyBelow is < 0 or > 1000)
            issues.Add(new("/stop_conditions/energy_below", "INVALID_LIMIT", "energy_below is outside the supported range."));
        if (plan.StopConditions.GameTimeAtOrAfter is < 600 or > 2600)
            issues.Add(new("/stop_conditions/game_time_at_or_after", "INVALID_LIMIT", "game_time_at_or_after must be between 600 and 2600."));
        if (plan.StopConditions.MaxFailures is < 0 or > 64)
            issues.Add(new("/stop_conditions/max_failures", "INVALID_LIMIT", "max_failures must be between 0 and 64."));
        return new(issues.Count == 0, issues);
    }

    private static void ValidateActionArgs(PlanAction action, string path, ICollection<ValidationIssue> issues)
    {
        if (action.Args.ValueKind != JsonValueKind.Object)
            return;

        bool HasString(string name) => action.Args.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString());

        switch (action.Action)
        {
            case "water_crop":
            case "harvest_crop":
                if (!HasString("target_id") || !HasString("target_revision"))
                    issues.Add(new($"{path}/args", "MISSING_TARGET", "A target_id and target_revision are required."));
                break;
            case "move_to":
                if (!action.Args.TryGetProperty("tile", out var tile) || tile.ValueKind != JsonValueKind.Array || tile.GetArrayLength() != 2)
                    issues.Add(new($"{path}/args/tile", "INVALID_TILE", "tile must contain exactly two integers."));
                break;
            case "wait":
                if (!action.Args.TryGetProperty("ticks", out var ticks) || !ticks.TryGetInt32(out var tickCount)
                    || tickCount is < 1 or > ProtocolLimits.MaxWaitTicks)
                    issues.Add(new($"{path}/args/ticks", "INVALID_WAIT", $"ticks must be between 1 and {ProtocolLimits.MaxWaitTicks}."));
                break;
            case "finish":
                if (!HasString("reason") || action.Args.GetProperty("reason").GetString()!.Length > 256)
                    issues.Add(new($"{path}/args/reason", "INVALID_REASON", "reason must contain 1 to 256 characters."));
                break;
        }
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace StardewAgent.Protocol;

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = SnakeCaseNamingPolicy.Instance,
        DictionaryKeyPolicy = SnakeCaseNamingPolicy.Instance,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false,
        MaxDepth = ProtocolLimits.MaxJsonDepth,
        WriteIndented = false
    };
}

internal sealed class SnakeCaseNamingPolicy : JsonNamingPolicy
{
    public static readonly SnakeCaseNamingPolicy Instance = new();

    public override string ConvertName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return name;
        var builder = new System.Text.StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var character = name[i];
            if (char.IsUpper(character))
            {
                if (i > 0 && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]))))
                    builder.Append('_');
                builder.Append(char.ToLowerInvariant(character));
            }
            else
            {
                builder.Append(character);
            }
        }
        return builder.ToString();
    }
}

public static class ProtocolLimits
{
    public const string ProtocolVersion = "1.0";
    public const string SchemaVersion = "2.0";
    public const int MaxLineBytes = 1024 * 1024;
    public const int MaxPlanBytes = 32 * 1024;
    public const int MaxJsonDepth = 12;
    public const int MaxActions = 64;
    public const int MaxMovementTiles = 800;
    public const int MaxToolUses = 64;
    public const int MaxRetriesPerAction = 2;
    public const int MaxPathReplans = 3;
    public const int MaxWaitTicks = 300;
    public static readonly TimeSpan MaxExecutionDuration = TimeSpan.FromSeconds(120);
}

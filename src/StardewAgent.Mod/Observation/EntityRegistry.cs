using System.Security.Cryptography;
using System.Text;

namespace StardewAgent.Mod.Observation;

internal static class EntityRegistry
{
    public static string Id(string location, string kind, int x, int y) => $"{location}/{kind}/{x},{y}";

    public static string Revision(params object?[] parts)
    {
        var text = string.Join('|', parts.Select(part => part?.ToString() ?? "null"));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }
}

using StardewAgent.Protocol;

namespace StardewAgent.Core.Navigation;

public enum TileClassification
{
    Walkable,
    StaticBlocked,
    DynamicBlocked,
    InteractionTarget,
    Discouraged,
    Warp,
    Unknown
}

public interface INavigationGrid
{
    int Width { get; }
    int Height { get; }
    TileClassification Classify(TilePoint tile);
}

public sealed record PathSearchOptions(
    bool AvoidWarps = true,
    int BaseStepCost = 10,
    int DiscouragedSurcharge = 15,
    int? MaxExpandedNodes = null);

public sealed record PathResult(
    bool Found,
    IReadOnlyList<TilePoint> Tiles,
    int Cost,
    int ExpandedNodes,
    string Code)
{
    public static PathResult NoPath(int expandedNodes, string code = "NO_PATH") =>
        new(false, Array.Empty<TilePoint>(), 0, expandedNodes, code);
}

public sealed record InteractionCandidate(TilePoint Tile, string Facing, int PathCost, int Turns);

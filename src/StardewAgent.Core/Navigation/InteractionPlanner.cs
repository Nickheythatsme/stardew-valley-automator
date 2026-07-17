using StardewAgent.Protocol;

namespace StardewAgent.Core.Navigation;

public sealed class InteractionPlanner
{
    private static readonly (TilePoint Offset, string Facing)[] Candidates =
    {
        (new TilePoint(0, -1), "down"),
        (new TilePoint(1, 0), "left"),
        (new TilePoint(0, 1), "up"),
        (new TilePoint(-1, 0), "right")
    };

    private readonly AStarPathPlanner pathPlanner;

    public InteractionPlanner(AStarPathPlanner pathPlanner) => this.pathPlanner = pathPlanner;

    public IReadOnlyList<InteractionCandidate> Rank(
        INavigationGrid grid,
        TilePoint player,
        TilePoint target,
        PathSearchOptions? options = null)
    {
        var results = new List<InteractionCandidate>();
        foreach (var (offset, facing) in Candidates)
        {
            var standing = new TilePoint(target.X + offset.X, target.Y + offset.Y);
            var path = pathPlanner.FindPath(grid, player, standing, options);
            if (!path.Found)
                continue;
            results.Add(new(standing, facing, path.Cost, CountTurns(path.Tiles)));
        }

        return results
            .OrderBy(candidate => candidate.PathCost)
            .ThenBy(candidate => candidate.Turns)
            .ThenBy(candidate => candidate.Tile.Y)
            .ThenBy(candidate => candidate.Tile.X)
            .ToArray();
    }

    private static int CountTurns(IReadOnlyList<TilePoint> path)
    {
        if (path.Count < 3)
            return 0;
        var turns = 0;
        var lastDx = path[1].X - path[0].X;
        var lastDy = path[1].Y - path[0].Y;
        for (var i = 2; i < path.Count; i++)
        {
            var dx = path[i].X - path[i - 1].X;
            var dy = path[i].Y - path[i - 1].Y;
            if (dx != lastDx || dy != lastDy)
                turns++;
            lastDx = dx;
            lastDy = dy;
        }
        return turns;
    }
}

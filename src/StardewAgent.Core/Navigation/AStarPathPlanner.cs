using StardewAgent.Protocol;

namespace StardewAgent.Core.Navigation;

public sealed class AStarPathPlanner
{
    private static readonly TilePoint[] NeighborOffsets =
    {
        new(0, -1), new(1, 0), new(0, 1), new(-1, 0)
    };

    public PathResult FindPath(
        INavigationGrid grid,
        TilePoint start,
        TilePoint goal,
        PathSearchOptions? options = null)
    {
        options ??= new PathSearchOptions();
        if (!InBounds(grid, start) || !InBounds(grid, goal))
            return PathResult.NoPath(0, "OUT_OF_BOUNDS");
        if (!CanStand(grid.Classify(goal), options.AvoidWarps))
            return PathResult.NoPath(0, "TARGET_UNREACHABLE");
        if (start == goal)
            return new(true, new[] { start }, 0, 0, "OK");

        var limit = options.MaxExpandedNodes ?? (int)Math.Ceiling(grid.Width * grid.Height * 1.1);
        var frontier = new PriorityQueue<TilePoint, PathPriority>();
        var cameFrom = new Dictionary<TilePoint, TilePoint>();
        var costs = new Dictionary<TilePoint, int> { [start] = 0 };
        frontier.Enqueue(start, Priority(start, goal, 0));
        var expanded = 0;

        while (frontier.Count > 0 && expanded < limit)
        {
            var current = frontier.Dequeue();
            if (current == goal)
                return BuildResult(cameFrom, costs[current], current, expanded);

            expanded++;
            foreach (var offset in NeighborOffsets)
            {
                var next = new TilePoint(current.X + offset.X, current.Y + offset.Y);
                if (!InBounds(grid, next))
                    continue;
                var classification = grid.Classify(next);
                if (!CanStand(classification, options.AvoidWarps))
                    continue;

                var step = options.BaseStepCost +
                    (classification == TileClassification.Discouraged ? options.DiscouragedSurcharge : 0);
                var newCost = costs[current] + step;
                if (costs.TryGetValue(next, out var oldCost) && newCost >= oldCost)
                    continue;

                costs[next] = newCost;
                cameFrom[next] = current;
                frontier.Enqueue(next, Priority(next, goal, newCost));
            }
        }

        return PathResult.NoPath(expanded, expanded >= limit ? "SEARCH_LIMIT_EXCEEDED" : "NO_PATH");
    }

    private static bool InBounds(INavigationGrid grid, TilePoint tile) =>
        tile.X >= 0 && tile.Y >= 0 && tile.X < grid.Width && tile.Y < grid.Height;

    private static bool CanStand(TileClassification classification, bool avoidWarps) => classification switch
    {
        TileClassification.Walkable => true,
        TileClassification.Discouraged => true,
        TileClassification.Warp => !avoidWarps,
        _ => false
    };

    private static PathPriority Priority(TilePoint tile, TilePoint goal, int cost) =>
        new(cost + Manhattan(tile, goal), Manhattan(tile, goal), tile.Y, tile.X);

    private static int Manhattan(TilePoint a, TilePoint b) =>
        Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);

    private static PathResult BuildResult(
        IReadOnlyDictionary<TilePoint, TilePoint> cameFrom,
        int cost,
        TilePoint goal,
        int expanded)
    {
        var path = new List<TilePoint> { goal };
        var current = goal;
        while (cameFrom.TryGetValue(current, out var previous))
        {
            path.Add(previous);
            current = previous;
        }
        path.Reverse();
        return new(true, path, cost, expanded, "OK");
    }

    private readonly record struct PathPriority(int Total, int Heuristic, int Y, int X) : IComparable<PathPriority>
    {
        public int CompareTo(PathPriority other)
        {
            var result = Total.CompareTo(other.Total);
            if (result != 0) return result;
            result = Heuristic.CompareTo(other.Heuristic);
            if (result != 0) return result;
            result = Y.CompareTo(other.Y);
            return result != 0 ? result : X.CompareTo(other.X);
        }
    }
}

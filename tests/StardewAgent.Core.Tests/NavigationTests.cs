using StardewAgent.Core.Navigation;
using StardewAgent.Protocol;
using Xunit;

namespace StardewAgent.Core.Tests;

public sealed class NavigationTests
{
    [Fact]
    public void FindsDeterministicPathAroundObstacle()
    {
        var grid = new TestGrid(5, 5, new[] { new TilePoint(1, 0), new TilePoint(1, 1) });
        var planner = new AStarPathPlanner();

        var result = planner.FindPath(grid, new TilePoint(0, 0), new TilePoint(2, 0));

        Assert.True(result.Found);
        Assert.Equal(new TilePoint(0, 0), result.Tiles[0]);
        Assert.Equal(new TilePoint(2, 0), result.Tiles[^1]);
        Assert.DoesNotContain(new TilePoint(1, 0), result.Tiles);
    }

    [Fact]
    public void RejectsBlockedTarget()
    {
        var target = new TilePoint(2, 2);
        var grid = new TestGrid(5, 5, new[] { target });

        var result = new AStarPathPlanner().FindPath(grid, new TilePoint(0, 0), target);

        Assert.False(result.Found);
        Assert.Equal("TARGET_UNREACHABLE", result.Code);
    }

    [Fact]
    public void RanksShortestInteractionPositionFirst()
    {
        var grid = new TestGrid(10, 10, Array.Empty<TilePoint>());
        var planner = new InteractionPlanner(new AStarPathPlanner());

        var result = planner.Rank(grid, new TilePoint(5, 2), new TilePoint(5, 5));

        Assert.Equal(new TilePoint(5, 4), result[0].Tile);
        Assert.Equal("down", result[0].Facing);
    }

    private sealed class TestGrid : INavigationGrid
    {
        private readonly HashSet<TilePoint> blocked;

        public TestGrid(int width, int height, IEnumerable<TilePoint> blocked)
        {
            Width = width;
            Height = height;
            this.blocked = blocked.ToHashSet();
        }

        public int Width { get; }
        public int Height { get; }

        public TileClassification Classify(TilePoint tile) =>
            blocked.Contains(tile) ? TileClassification.StaticBlocked : TileClassification.Walkable;
    }
}

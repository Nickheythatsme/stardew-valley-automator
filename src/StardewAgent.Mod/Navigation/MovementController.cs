using Microsoft.Xna.Framework;
using StardewAgent.Core.Navigation;
using StardewAgent.Mod.Game;
using StardewAgent.Protocol;
using StardewModdingAPI;
using StardewValley;

namespace StardewAgent.Mod.Navigation;

internal sealed class MovementController
{
    private readonly IGameFacade game;
    private readonly AStarPathPlanner planner = new();
    private IReadOnlyList<TilePoint> path = Array.Empty<TilePoint>();
    private int waypointIndex;
    private Vector2 previousStanding;
    private int stationaryTicks;
    private long startedTick;
    private Vector2 startedStanding;
    private TilePoint? requestedTarget;
    private int injectedTicks;

    public MovementController(IGameFacade game) => this.game = game;

    public bool Running { get; private set; }
    public bool Completed { get; private set; }
    public string? FailureCode { get; private set; }
    public int TraversedTiles { get; private set; }
    public SButton? LastInjectedButton { get; private set; }
    public string Diagnostic
    {
        get
        {
            var standing = game.Player.getStandingPosition();
            var target = requestedTarget ?? new TilePoint(-1, -1);
            var waypoint = waypointIndex < path.Count ? path[waypointIndex] : target;
            return $"target={target.X},{target.Y}; waypoint={waypoint.X},{waypoint.Y}; " +
                   $"start={startedStanding.X:0.0},{startedStanding.Y:0.0}; current={standing.X:0.0},{standing.Y:0.0}; " +
                   $"button={LastInjectedButton?.ToString() ?? "none"}; injected_ticks={injectedTicks}; " +
                   $"stationary_ticks={stationaryTicks}; game_window_active={game.IsGameWindowActive}; " +
                   $"input_suspended={game.InputSuspended}";
        }
    }

    public bool Start(TilePoint target, bool allowWarp = false)
    {
        Reset();
        requestedTarget = target;
        var player = game.Player;
        var grid = new GameNavigationGrid(game.Location, player);
        var start = new TilePoint(player.TilePoint.X, player.TilePoint.Y);
        var result = planner.FindPath(grid, start, target, new PathSearchOptions(AvoidWarps: !allowWarp));
        if (!result.Found)
        {
            FailureCode = result.Code;
            return false;
        }
        path = result.Tiles;
        waypointIndex = path.Count > 1 ? 1 : 0;
        previousStanding = player.getStandingPosition();
        startedStanding = previousStanding;
        startedTick = game.Tick;
        Completed = path.Count == 1 && IsCentered(path[0]);
        Running = !Completed;
        return true;
    }

    public SButton? InputForCurrentTick()
    {
        LastInjectedButton = null;
        if (!Running || waypointIndex >= path.Count)
            return null;
        var standing = game.Player.getStandingPosition();
        var target = path[waypointIndex];
        var targetPixel = new Vector2(target.X * Game1.tileSize + Game1.tileSize / 2, target.Y * Game1.tileSize + Game1.tileSize / 2);
        var dx = targetPixel.X - standing.X;
        var dy = targetPixel.Y - standing.Y;
        SButton button;
        if (Math.Abs(dx) > 5)
            button = dx > 0 ? MoveRight() : MoveLeft();
        else if (Math.Abs(dy) > 5)
            button = dy > 0 ? MoveDown() : MoveUp();
        else
            return null;
        LastInjectedButton = button;
        injectedTicks++;
        return button;
    }

    public void Tick()
    {
        if (!Running)
            return;
        var standing = game.Player.getStandingPosition();
        if (DistanceSquared(standing, previousStanding) < 4)
            stationaryTicks++;
        else
            stationaryTicks = 0;
        previousStanding = standing;

        if (game.Tick - startedTick > 60 * 30)
        {
            Fail("MOVEMENT_TIMEOUT");
            return;
        }
        if (stationaryTicks >= 15)
        {
            Fail("STUCK");
            return;
        }

        while (waypointIndex < path.Count && IsCentered(path[waypointIndex]))
        {
            waypointIndex++;
            TraversedTiles++;
        }
        if (waypointIndex >= path.Count)
        {
            Running = false;
            Completed = true;
            LastInjectedButton = null;
        }
    }

    public void Cancel(string code = "CANCELLED")
    {
        if (Running)
            Fail(code);
    }

    private bool IsCentered(TilePoint tile)
    {
        var standing = game.Player.getStandingPosition();
        var targetX = tile.X * Game1.tileSize + Game1.tileSize / 2;
        var targetY = tile.Y * Game1.tileSize + Game1.tileSize / 2;
        return Math.Abs(standing.X - targetX) <= 5 && Math.Abs(standing.Y - targetY) <= 5;
    }

    private void Reset()
    {
        path = Array.Empty<TilePoint>();
        waypointIndex = 0;
        stationaryTicks = 0;
        TraversedTiles = 0;
        Running = false;
        Completed = false;
        FailureCode = null;
        LastInjectedButton = null;
        startedStanding = Vector2.Zero;
        requestedTarget = default;
        injectedTicks = 0;
    }

    private void Fail(string code)
    {
        Running = false;
        Completed = false;
        FailureCode = code;
        LastInjectedButton = null;
    }

    private static float DistanceSquared(Vector2 left, Vector2 right)
    {
        var dx = left.X - right.X;
        var dy = left.Y - right.Y;
        return dx * dx + dy * dy;
    }

    private static SButton MoveUp() => Game1.options.moveUpButton.Count() > 0 ? Game1.options.moveUpButton[0].ToSButton() : SButton.W;
    private static SButton MoveRight() => Game1.options.moveRightButton.Count() > 0 ? Game1.options.moveRightButton[0].ToSButton() : SButton.D;
    private static SButton MoveDown() => Game1.options.moveDownButton.Count() > 0 ? Game1.options.moveDownButton[0].ToSButton() : SButton.S;
    private static SButton MoveLeft() => Game1.options.moveLeftButton.Count() > 0 ? Game1.options.moveLeftButton[0].ToSButton() : SButton.A;
}

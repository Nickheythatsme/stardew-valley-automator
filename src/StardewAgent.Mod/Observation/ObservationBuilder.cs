using Microsoft.Xna.Framework;
using StardewAgent.Core.Navigation;
using StardewAgent.Mod.Game;
using StardewAgent.Mod.Navigation;
using StardewAgent.Protocol;
using StardewValley;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;
using ProtocolObservation = StardewAgent.Protocol.Observation;

namespace StardewAgent.Mod.Observation;

internal sealed class ObservationBuilder
{
    private const int MaxEntities = 128;
    private readonly IGameFacade game;
    private readonly AStarPathPlanner pathPlanner = new();
    private readonly InteractionPlanner interactionPlanner;
    private long observationSequence;

    public ObservationBuilder(IGameFacade game)
    {
        this.game = game;
        interactionPlanner = new(pathPlanner);
    }

    public string? CurrentObservationId { get; private set; }
    public long WorldRevision { get; private set; }
    public ProtocolObservation? LastObservation { get; private set; }

    public void Invalidate() => WorldRevision++;

    public ProtocolObservation Build(int radius = 10, ExecutionSummary? previousExecution = null)
    {
        if (!game.WorldReady)
            throw new GameStateException("WORLD_NOT_READY", "Load a save before requesting an observation.");
        if (!game.IsSinglePlayer)
            throw new GameStateException("MULTIPLAYER_UNSUPPORTED", "The prototype supports single-player only.");
        if (!game.IsFarm)
            throw new GameStateException("UNSUPPORTED_LOCATION", "The prototype supports the Farm location only.");

        radius = Math.Clamp(radius, 5, 15);
        var player = game.Player;
        var location = game.Location;
        var grid = new GameNavigationGrid(location, player);
        var playerTile = new TilePoint(player.TilePoint.X, player.TilePoint.Y);
        var entities = BuildEntities(location, grid, playerTile);
        var localGrid = BuildLocalGrid(grid, playerTile, radius, entities);
        var inventory = BuildInventory(player);
        var observationId = $"obs-{Interlocked.Increment(ref observationSequence):D8}";
        var observation = new ProtocolObservation(
            ProtocolLimits.SchemaVersion,
            observationId,
            WorldRevision,
            DateTimeOffset.UtcNow,
            new GameState(
                Game1.uniqueIDForThisGame.ToString(),
                Game1.timeOfDay,
                Game1.currentSeason,
                Game1.dayOfMonth,
                Game1.year,
                Game1.isRaining ? "rain" : "sunny",
                location.NameOrUniqueName,
                true,
                Game1.paused,
                Game1.activeClickableMenu?.GetType().Name,
                Game1.CurrentEvent is not null),
            new PlayerState(
                playerTile,
                new PixelPoint(player.Position.X, player.Position.Y),
                Facing(player.FacingDirection),
                player.Stamina,
                player.MaxStamina,
                player.health,
                player.maxHealth,
                player.Money,
                player.IsBusyDoingSomething(),
                player.UsingTool,
                player.CurrentToolIndex),
            inventory,
            localGrid,
            entities,
            new ObservationSummary(
                entities.Count(entity => entity.Kind == "crop" && Bool(entity, "needs_watering") && !Bool(entity, "watered")),
                entities.Count(entity => entity.Kind == "crop" && Bool(entity, "harvestable")),
                entities.Count(entity => entity.Kind == "tilled_soil"),
                entities.Count(entity => entity.Kind == "water_source" && entity.Reachable),
                entities.Count(entity => entity.Kind == "container"),
                new Dictionary<string, int>()),
            previousExecution);
        CurrentObservationId = observationId;
        LastObservation = observation;
        return observation;
    }

    private IReadOnlyList<Entity> BuildEntities(GameLocation location, GameNavigationGrid grid, TilePoint playerTile)
    {
        var candidates = new List<Entity>();
        foreach (var pair in location.terrainFeatures.Pairs)
        {
            if (pair.Value is not HoeDirt dirt)
                continue;
            var tile = new TilePoint((int)pair.Key.X, (int)pair.Key.Y);
            var crop = dirt.crop;
            var kind = crop is null ? "tilled_soil" : "crop";
            var interactions = InteractionTiles(grid, playerTile, tile);
            var best = interactions.FirstOrDefault(item => item.Reachable);
            if (crop is null)
            {
                candidates.Add(new Entity(
                    EntityRegistry.Id("Farm", kind, tile.X, tile.Y),
                    EntityRegistry.Revision(kind, tile.X, tile.Y, dirt.isWatered()),
                    kind, "Farm", tile, best is not null, best?.PathCost, interactions,
                    new Dictionary<string, object?> { ["watered"] = dirt.isWatered() }));
                continue;
            }

            var mature = crop.currentPhase.Value >= crop.phaseDays.Count - 1;
            var harvestable = mature && (!crop.fullyGrown.Value || crop.dayOfCurrentPhase.Value <= 0);
            var properties = new Dictionary<string, object?>
            {
                ["crop_id"] = crop.indexOfHarvest.Value,
                ["display_name"] = null,
                ["watered"] = dirt.isWatered(),
                ["needs_watering"] = dirt.needsWatering(),
                ["mature"] = mature,
                ["harvestable"] = harvestable,
                ["harvest_method"] = "hand"
            };
            candidates.Add(new Entity(
                EntityRegistry.Id("Farm", kind, tile.X, tile.Y),
                EntityRegistry.Revision(kind, tile.X, tile.Y, crop.indexOfHarvest.Value, dirt.isWatered(), mature, harvestable),
                kind, "Farm", tile, best is not null, best?.PathCost, interactions, properties));
        }

        foreach (var pair in location.Objects.Pairs)
        {
            if (pair.Value is not Chest chest)
                continue;
            var tile = new TilePoint((int)pair.Key.X, (int)pair.Key.Y);
            var interactions = InteractionTiles(grid, playerTile, tile);
            var best = interactions.FirstOrDefault(item => item.Reachable);
            candidates.Add(new Entity(
                EntityRegistry.Id("Farm", "container", tile.X, tile.Y),
                EntityRegistry.Revision("container", tile.X, tile.Y, chest.Items.Count, chest.Name),
                "container", "Farm", tile, best is not null, best?.PathCost, interactions,
                new Dictionary<string, object?>
                {
                    ["label"] = chest.Name,
                    ["capacity"] = 36,
                    ["occupied_slots"] = chest.Items.Count(item => item is not null)
                }));
        }

        AddWaterSources(location, grid, playerTile, candidates);
        return candidates
            .OrderByDescending(entity => entity.Reachable)
            .ThenBy(entity => entity.PathCost ?? int.MaxValue)
            .ThenBy(entity => entity.Id, StringComparer.Ordinal)
            .Take(MaxEntities)
            .ToArray();
    }

    private void AddWaterSources(GameLocation location, GameNavigationGrid grid, TilePoint playerTile, ICollection<Entity> candidates)
    {
        var added = 0;
        for (var y = 0; y < grid.Height && added < 16; y++)
        {
            for (var x = 0; x < grid.Width && added < 16; x++)
            {
                if (!location.isWaterTile(x, y))
                    continue;
                var tile = new TilePoint(x, y);
                var interactions = InteractionTiles(grid, playerTile, tile);
                var best = interactions.FirstOrDefault(item => item.Reachable);
                if (best is null)
                    continue;
                candidates.Add(new Entity(
                    EntityRegistry.Id("Farm", "water_source", x, y),
                    EntityRegistry.Revision("water_source", x, y),
                    "water_source", "Farm", tile, true, best.PathCost, interactions,
                    new Dictionary<string, object?> { ["interaction_tile"] = best.Tile }));
                added++;
            }
        }
    }

    private IReadOnlyList<InteractionTile> InteractionTiles(GameNavigationGrid grid, TilePoint player, TilePoint target)
    {
        var ranked = interactionPlanner.Rank(grid, player, target);
        var byTile = ranked.ToDictionary(candidate => candidate.Tile);
        var raw = new[]
        {
            (new TilePoint(target.X, target.Y - 1), "down"),
            (new TilePoint(target.X + 1, target.Y), "left"),
            (new TilePoint(target.X, target.Y + 1), "up"),
            (new TilePoint(target.X - 1, target.Y), "right")
        };
        return raw.Select(item => byTile.TryGetValue(item.Item1, out var candidate)
            ? new InteractionTile(item.Item1, item.Item2, true, candidate.PathCost)
            : new InteractionTile(item.Item1, item.Item2, false, null)).ToArray();
    }

    private static IReadOnlyList<InventoryStack> BuildInventory(Farmer player)
    {
        var result = new List<InventoryStack>();
        for (var slot = 0; slot < player.Items.Count; slot++)
        {
            var item = player.Items[slot];
            if (item is null)
                continue;
            result.Add(new InventoryStack(
                slot,
                item.QualifiedItemId,
                item.DisplayName,
                item is Tool ? "tool" : "item",
                item.Stack,
                item is Tool tool ? tool.UpgradeLevel : null,
                item is WateringCan can ? can.WaterLeft : null,
                item is WateringCan maxCan ? maxCan.waterCanMax : null));
        }
        return result;
    }

    private static LocalGrid BuildLocalGrid(
        GameNavigationGrid grid,
        TilePoint player,
        int radius,
        IReadOnlyCollection<Entity> entities)
    {
        var minX = Math.Max(0, player.X - radius);
        var minY = Math.Max(0, player.Y - radius);
        var maxX = Math.Min(grid.Width - 1, player.X + radius);
        var maxY = Math.Min(grid.Height - 1, player.Y + radius);
        var entityTiles = entities.ToDictionary(entity => entity.Tile, entity => entity.Kind);
        var rows = new List<string>();
        for (var y = minY; y <= maxY; y++)
        {
            var row = new char[maxX - minX + 1];
            for (var x = minX; x <= maxX; x++)
            {
                var tile = new TilePoint(x, y);
                row[x - minX] = tile == player ? '@' : Symbol(grid.Classify(tile));
                if (tile != player && entityTiles.TryGetValue(tile, out var kind))
                    row[x - minX] = kind switch { "crop" => 'c', "container" => 'C', "water_source" => '~', _ => row[x - minX] };
            }
            rows.Add(new string(row));
        }
        return new LocalGrid(
            new TilePoint(minX, minY),
            maxX - minX + 1,
            maxY - minY + 1,
            rows,
            new Dictionary<string, string>
            {
                ["."] = "walkable", ["#"] = "static blocked", ["D"] = "dynamic blocked",
                ["d"] = "discouraged", ["X"] = "warp", ["?"] = "unknown", ["@"] = "player",
                ["c"] = "crop", ["C"] = "container", ["~"] = "water source"
            });
    }

    private static char Symbol(TileClassification classification) => classification switch
    {
        TileClassification.Walkable => '.',
        TileClassification.StaticBlocked => '#',
        TileClassification.DynamicBlocked => 'D',
        TileClassification.Discouraged => 'd',
        TileClassification.Warp => 'X',
        _ => '?'
    };

    private static bool Bool(Entity entity, string property) =>
        entity.Properties.TryGetValue(property, out var value) && value is true;

    private static string Facing(int direction) => direction switch
    {
        Game1.up => "up", Game1.right => "right", Game1.down => "down", Game1.left => "left", _ => "down"
    };
}

internal sealed class GameStateException : Exception
{
    public GameStateException(string code, string message) : base(message) => Code = code;
    public string Code { get; }
}

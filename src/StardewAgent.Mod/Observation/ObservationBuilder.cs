using Microsoft.Xna.Framework;
using StardewAgent.Core.Navigation;
using StardewAgent.Mod.Game;
using StardewAgent.Mod.Navigation;
using StardewAgent.Protocol;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Locations;
using StardewValley.Menus;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;
using ProtocolObservation = StardewAgent.Protocol.Observation;

namespace StardewAgent.Mod.Observation;

internal sealed class ObservationBuilder
{
    private const int MaxEntities = 128;
    private readonly IGameFacade game;
    private IReadOnlyDictionary<TilePoint, int> reachability =
        new Dictionary<TilePoint, int>();
    private long observationSequence;
    private IReadOnlyDictionary<string, int> omittedEntities = new Dictionary<string, int>();

    public ObservationBuilder(IGameFacade game)
    {
        this.game = game;
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
        if (!game.IsSupportedLocation)
            throw new GameStateException("UNSUPPORTED_LOCATION", "This goal runner supports Farm, FarmHouse, BusStop, Town, and SeedShop.");

        radius = Math.Clamp(radius, 5, 15);
        var player = game.Player;
        var location = game.Location;
        var grid = new GameNavigationGrid(location, player);
        var playerTile = new TilePoint(player.TilePoint.X, player.TilePoint.Y);
        reachability = BuildReachability(grid, playerTile);
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
                Game1.paused || game.InputSuspended,
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
                entities.Count(entity => entity.Kind == "planting_tile"),
                entities.Count(entity => entity.Kind == "water_source" && entity.Reachable),
                entities.Count(entity => entity.Kind == "container"),
                omittedEntities),
            Routes(location),
            BuildUiState(),
            BuildShopOffers(),
            new EconomyState(
                player.Money,
                PendingShippingValue(),
                location.NameOrUniqueName == "SeedShop" && Game1.timeOfDay is >= 900 and < 1700,
                Game1.timeOfDay < 900 ? 900 : null),
            previousExecution);
        CurrentObservationId = observationId;
        LastObservation = observation;
        return observation;
    }

    private static int PendingShippingValue()
    {
        return Game1.getFarm().getShippingBin(Game1.player)
            .Where(item => item is StardewValley.Object)
            .Sum(item => item.sellToStorePrice() * item.Stack);
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
            var kind = crop is null ? "planting_tile" : "crop";
            var interactions = InteractionTiles(grid, playerTile, tile);
            var best = interactions.FirstOrDefault(item => item.Reachable);
            if (crop is null)
            {
                candidates.Add(new Entity(
                    EntityRegistry.Id(location.NameOrUniqueName, kind, tile.X, tile.Y),
                    EntityRegistry.Revision(kind, tile.X, tile.Y, dirt.isWatered()),
                    kind, location.NameOrUniqueName, tile, best is not null, best?.PathCost, interactions,
                    new Dictionary<string, object?>
                    {
                        ["watered"] = dirt.isWatered(),
                        ["tilled"] = true,
                        ["occupied"] = false
                    }));
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
                EntityRegistry.Id(location.NameOrUniqueName, kind, tile.X, tile.Y),
                EntityRegistry.Revision(kind, tile.X, tile.Y, crop.indexOfHarvest.Value, dirt.isWatered(), mature, harvestable),
                kind, location.NameOrUniqueName, tile, best is not null, best?.PathCost, interactions, properties));
        }

        foreach (var pair in location.Objects.Pairs)
        {
            if (pair.Value is not Chest chest)
                continue;
            var tile = new TilePoint((int)pair.Key.X, (int)pair.Key.Y);
            var interactions = InteractionTiles(grid, playerTile, tile);
            var best = interactions.FirstOrDefault(item => item.Reachable);
            candidates.Add(new Entity(
                EntityRegistry.Id(location.NameOrUniqueName, "container", tile.X, tile.Y),
                EntityRegistry.Revision("container", tile.X, tile.Y, chest.Items.Count, chest.Name),
                "container", location.NameOrUniqueName, tile, best is not null, best?.PathCost, interactions,
                new Dictionary<string, object?>
                {
                    ["label"] = chest.Name,
                    ["capacity"] = 36,
                    ["occupied_slots"] = chest.Items.Count(item => item is not null)
                }));
        }

        if (location.NameOrUniqueName == "Farm")
        {
            AddUntilledPlantingTiles(location, grid, playerTile, candidates);
            AddDebris(location, grid, playerTile, candidates);
            AddWaterSources(location, grid, playerTile, candidates);
            AddShippingBins(location, grid, playerTile, candidates);
        }
        AddActionEntities(location, grid, playerTile, candidates);
        AddBeds(location, grid, playerTile, candidates);
        if (location.NameOrUniqueName != "Farm")
            AddRemoteFarmEntities(candidates);
        var ordered = candidates
            .OrderByDescending(entity => entity.Reachable)
            .ThenBy(entity => entity.PathCost ?? int.MaxValue)
            .ThenBy(entity => entity.Id, StringComparer.Ordinal);
        var selected = ordered.Take(MaxEntities).ToArray();
        omittedEntities = candidates
            .Except(selected)
            .GroupBy(entity => entity.Kind)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        return selected;
    }

    private void AddBeds(
        GameLocation location,
        GameNavigationGrid grid,
        TilePoint playerTile,
        ICollection<Entity> candidates)
    {
        if (location.NameOrUniqueName != "FarmHouse")
            return;
        for (var y = 0; y < grid.Height; y++)
        {
            for (var x = 0; x < grid.Width; x++)
            {
                if (!BedFurniture.IsBedHere(location, x, y))
                    continue;
                var tile = new TilePoint(x, y);
                var interactions = InteractionTiles(grid, playerTile, tile);
                var best = interactions.FirstOrDefault(item => item.Reachable);
                candidates.Add(new Entity(
                    EntityRegistry.Id("FarmHouse", "bed", x, y),
                    EntityRegistry.Revision("bed", x, y),
                    "bed", "FarmHouse", tile, best is not null, best?.PathCost, interactions,
                    new Dictionary<string, object?>()));
            }
        }
    }

    private static void AddRemoteFarmEntities(ICollection<Entity> candidates)
    {
        var farm = Game1.getFarm();
        foreach (var pair in farm.terrainFeatures.Pairs)
        {
            if (pair.Value is not HoeDirt dirt)
                continue;
            var tile = new TilePoint((int)pair.Key.X, (int)pair.Key.Y);
            if (dirt.crop is not { } crop)
            {
                candidates.Add(new Entity(
                    EntityRegistry.Id("Farm", "planting_tile", tile.X, tile.Y),
                    EntityRegistry.Revision("planting_tile", tile.X, tile.Y, true, dirt.isWatered()),
                    "planting_tile", "Farm", tile, false, null, Array.Empty<InteractionTile>(),
                    new Dictionary<string, object?>
                    {
                        ["watered"] = dirt.isWatered(), ["tilled"] = true, ["occupied"] = false
                    }));
                continue;
            }
            var mature = crop.currentPhase.Value >= crop.phaseDays.Count - 1;
            var harvestable = mature && (!crop.fullyGrown.Value || crop.dayOfCurrentPhase.Value <= 0);
            candidates.Add(new Entity(
                EntityRegistry.Id("Farm", "crop", tile.X, tile.Y),
                EntityRegistry.Revision(
                    "crop", tile.X, tile.Y, crop.indexOfHarvest.Value,
                    dirt.isWatered(), mature, harvestable),
                "crop", "Farm", tile, false, null, Array.Empty<InteractionTile>(),
                new Dictionary<string, object?>
                {
                    ["crop_id"] = crop.indexOfHarvest.Value,
                    ["display_name"] = null,
                    ["watered"] = dirt.isWatered(),
                    ["needs_watering"] = dirt.needsWatering(),
                    ["mature"] = mature,
                    ["harvestable"] = harvestable,
                    ["harvest_method"] = "hand"
                }));
        }
    }

    private void AddShippingBins(
        GameLocation location,
        GameNavigationGrid grid,
        TilePoint playerTile,
        ICollection<Entity> candidates)
    {
        if (location is not Farm farm)
            return;
        foreach (var building in farm.buildings.Where(building => building is ShippingBin))
        {
            var tile = new TilePoint(building.tileX.Value + 1, building.tileY.Value + 1);
            var interactions = InteractionTiles(grid, playerTile, tile);
            var best = interactions.FirstOrDefault(item => item.Reachable);
            candidates.Add(new Entity(
                EntityRegistry.Id("Farm", "shipping_bin", tile.X, tile.Y),
                EntityRegistry.Revision("shipping_bin", tile.X, tile.Y),
                "shipping_bin", "Farm", tile, best is not null, best?.PathCost, interactions,
                new Dictionary<string, object?> { ["building_type"] = building.buildingType.Value }));
        }
    }

    private void AddActionEntities(
        GameLocation location,
        GameNavigationGrid grid,
        TilePoint playerTile,
        ICollection<Entity> candidates)
    {
        for (var y = 0; y < grid.Height; y++)
        {
            for (var x = 0; x < grid.Width; x++)
            {
                var action = location.doesTileHaveProperty(x, y, "Action", "Buildings")
                    ?? location.doesTileHaveProperty(x, y, "TouchAction", "Back");
                if (string.IsNullOrWhiteSpace(action))
                    continue;
                var kind = action.Contains("OpenShop", StringComparison.OrdinalIgnoreCase)
                    || action.Contains("Buy", StringComparison.OrdinalIgnoreCase)
                    ? "shop"
                    : action.Contains("Shipping", StringComparison.OrdinalIgnoreCase)
                        ? "shipping_bin"
                        : action.Contains("Sleep", StringComparison.OrdinalIgnoreCase)
                            ? "bed"
                            : null;
                if (kind is null)
                    continue;
                var tile = new TilePoint(x, y);
                var interactions = InteractionTiles(grid, playerTile, tile);
                var best = interactions.FirstOrDefault(item => item.Reachable);
                candidates.Add(new Entity(
                    EntityRegistry.Id(location.NameOrUniqueName, kind, x, y),
                    EntityRegistry.Revision(kind, x, y, action),
                    kind, location.NameOrUniqueName, tile, best is not null, best?.PathCost, interactions,
                    new Dictionary<string, object?> { ["action"] = action }));
            }
        }
    }

    private static IReadOnlyList<ShopOffer> BuildShopOffers()
    {
        if (Game1.activeClickableMenu is not ShopMenu shop)
            return Array.Empty<ShopOffer>();
        var result = new List<ShopOffer>();
        for (var index = 0; index < shop.forSale.Count && result.Count < 128; index++)
        {
            if (shop.forSale[index] is not Item item
                || !shop.itemPriceAndStock.TryGetValue(shop.forSale[index], out var stock))
                continue;
            var isSeed = item.Category == StardewValley.Object.SeedsCategory;
            StardewValley.Crop.TryGetData(item.ItemId, out var crop);
            var harvestMethod = crop is null ? null
                : crop.HarvestMethod == StardewValley.GameData.Crops.HarvestMethod.Grab
                    ? "hand"
                    : "scythe";
            var offerId = $"{shop.ShopId}/offer/{index}/{item.QualifiedItemId}";
            result.Add(new ShopOffer(
                offerId,
                EntityRegistry.Revision("offer", shop.ShopId, index, item.QualifiedItemId, stock.Price, stock.Stock),
                shop.ShopId ?? "unknown",
                item.QualifiedItemId,
                item.DisplayName,
                stock.Price,
                stock.Stock == ShopMenu.infiniteStock ? null : stock.Stock,
                stock.Stock != 0 && index < 4,
                isSeed,
                crop?.HarvestItemId,
                crop?.DaysInPhase.Sum(),
                crop?.Seasons.Select(season => season.ToString().ToLowerInvariant()).ToArray()
                    ?? Array.Empty<string>(),
                harvestMethod,
                crop?.IsRaised ?? false,
                crop is null || crop.RegrowDays < 0 ? null : crop.RegrowDays));
        }
        return result;
    }

    private void AddUntilledPlantingTiles(
        GameLocation location,
        GameNavigationGrid grid,
        TilePoint playerTile,
        ICollection<Entity> candidates)
    {
        var added = candidates.Count(entity => entity.Kind == "planting_tile");
        for (var distance = 0; distance <= 20 && added < 16; distance++)
        {
            for (var y = Math.Max(0, playerTile.Y - distance); y <= Math.Min(grid.Height - 1, playerTile.Y + distance) && added < 16; y++)
            {
                for (var x = Math.Max(0, playerTile.X - distance); x <= Math.Min(grid.Width - 1, playerTile.X + distance) && added < 16; x++)
                {
                    if (Math.Abs(x - playerTile.X) + Math.Abs(y - playerTile.Y) != distance)
                        continue;
                    var point = new Vector2(x, y);
                    if (location.terrainFeatures.ContainsKey(point) || location.Objects.ContainsKey(point))
                        continue;
                    if (location.doesTileHaveProperty(x, y, "Diggable", "Back") is null)
                        continue;
                    var tile = new TilePoint(x, y);
                    var interactions = InteractionTiles(grid, playerTile, tile);
                    var best = interactions.FirstOrDefault(item => item.Reachable);
                    if (best is null)
                        continue;
                    candidates.Add(new Entity(
                        EntityRegistry.Id("Farm", "planting_tile", x, y),
                        EntityRegistry.Revision("planting_tile", x, y, false, false),
                        "planting_tile", "Farm", tile, true, best.PathCost, interactions,
                        new Dictionary<string, object?>
                        {
                            ["watered"] = false,
                            ["tilled"] = false,
                            ["occupied"] = false
                        }));
                    added++;
                }
            }
        }
    }

    private void AddDebris(
        GameLocation location,
        GameNavigationGrid grid,
        TilePoint playerTile,
        ICollection<Entity> candidates)
    {
        foreach (var pair in location.Objects.Pairs
                     .OrderBy(pair => Math.Abs(pair.Key.X - playerTile.X) + Math.Abs(pair.Key.Y - playerTile.Y))
                     .Take(32))
        {
            if (pair.Value is Chest)
                continue;
            var tool = pair.Value.Name.Contains("Stone", StringComparison.OrdinalIgnoreCase)
                ? "pickaxe"
                : pair.Value.Name.Contains("Twig", StringComparison.OrdinalIgnoreCase)
                  || pair.Value.Name.Contains("Branch", StringComparison.OrdinalIgnoreCase)
                    ? "axe"
                    : pair.Value.Name.Contains("Weed", StringComparison.OrdinalIgnoreCase)
                        ? "scythe"
                        : null;
            if (tool is null)
                continue;
            var tile = new TilePoint((int)pair.Key.X, (int)pair.Key.Y);
            var interactions = InteractionTiles(grid, playerTile, tile);
            var best = interactions.FirstOrDefault(item => item.Reachable);
            candidates.Add(new Entity(
                EntityRegistry.Id("Farm", "debris", tile.X, tile.Y),
                EntityRegistry.Revision("debris", tile.X, tile.Y, pair.Value.QualifiedItemId),
                "debris", "Farm", tile, best is not null, best?.PathCost, interactions,
                new Dictionary<string, object?>
                {
                    ["qualified_item_id"] = pair.Value.QualifiedItemId,
                    ["name"] = pair.Value.DisplayName,
                    ["required_tool"] = tool
                }));
        }
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
                    EntityRegistry.Id(location.NameOrUniqueName, "water_source", x, y),
                    EntityRegistry.Revision("water_source", x, y),
                    "water_source", location.NameOrUniqueName, tile, true, best.PathCost, interactions,
                    new Dictionary<string, object?> { ["interaction_tile"] = best.Tile }));
                added++;
            }
        }
    }

    private static IReadOnlyList<RouteEdge> Routes(GameLocation currentLocation)
    {
        var supported = new HashSet<string>(StringComparer.Ordinal)
        {
            "Farm", "FarmHouse", "BusStop", "Town", "SeedShop"
        };
        var width = currentLocation.Map.Layers[0].LayerWidth;
        var height = currentLocation.Map.Layers[0].LayerHeight;
        var routes = currentLocation.warps
            .Where(warp => supported.Contains(warp.TargetName))
            .Select(warp => new RouteEdge(
                currentLocation.NameOrUniqueName,
                warp.TargetName,
                new TilePoint(
                    Math.Clamp(warp.X, 0, width - 1),
                    Math.Clamp(warp.Y, 0, height - 1)),
                new TilePoint(warp.TargetX, warp.TargetY)))
            .ToList();

        // The main farmhouse is represented by a Farm building rather than a
        // normal map warp, so it isn't present in GameLocation.warps. Expose its
        // public door tile as a first-class route so the planner can go home.
        if (currentLocation is Farm farm
            && routes.All(route => route.ToLocation != "FarmHouse"))
        {
            var entry = farm.GetMainFarmHouseEntry();
            routes.Add(new RouteEdge(
                "Farm",
                "FarmHouse",
                new TilePoint((int)entry.X, (int)entry.Y),
                new TilePoint(0, 0)));
        }

        return routes
            .OrderBy(route => route.ToLocation, StringComparer.Ordinal)
            .ThenBy(route => route.DepartureTile.Y)
            .ThenBy(route => route.DepartureTile.X)
            .ToArray();
    }

    private static UiState? BuildUiState()
    {
        var menu = Game1.activeClickableMenu;
        if (menu is null)
            return null;
        var menuType = menu.GetType().Name;
        var kind = menuType.Contains("Shop", StringComparison.OrdinalIgnoreCase) ? "shop"
            : menuType.Contains("Dialogue", StringComparison.OrdinalIgnoreCase) ? "dialogue"
            : menuType.Contains("Shipping", StringComparison.OrdinalIgnoreCase) ? "shipping"
            : menuType.Contains("LevelUp", StringComparison.OrdinalIgnoreCase) ? "level_up"
            : "unknown";
        return new UiState(kind, menuType, null, Array.Empty<UiChoice>(), false);
    }

    private IReadOnlyList<InteractionTile> InteractionTiles(GameNavigationGrid grid, TilePoint player, TilePoint target)
    {
        var raw = new[]
        {
            (new TilePoint(target.X, target.Y - 1), "down"),
            (new TilePoint(target.X + 1, target.Y), "left"),
            (new TilePoint(target.X, target.Y + 1), "up"),
            (new TilePoint(target.X - 1, target.Y), "right")
        };
        return raw.Select(item => reachability.TryGetValue(item.Item1, out var cost)
            ? new InteractionTile(item.Item1, item.Item2, true, cost)
            : new InteractionTile(item.Item1, item.Item2, false, null)).ToArray();
    }

    private static IReadOnlyDictionary<TilePoint, int> BuildReachability(
        GameNavigationGrid grid,
        TilePoint start)
    {
        var costs = new Dictionary<TilePoint, int> { [start] = 0 };
        var frontier = new PriorityQueue<TilePoint, int>();
        frontier.Enqueue(start, 0);
        var offsets = new[]
        {
            new TilePoint(0, -1), new TilePoint(1, 0),
            new TilePoint(0, 1), new TilePoint(-1, 0)
        };
        while (frontier.TryDequeue(out var current, out var queuedCost))
        {
            if (costs[current] != queuedCost)
                continue;
            foreach (var offset in offsets)
            {
                var next = new TilePoint(current.X + offset.X, current.Y + offset.Y);
                if (next.X < 0 || next.Y < 0 || next.X >= grid.Width || next.Y >= grid.Height)
                    continue;
                var classification = grid.Classify(next);
                if (classification is not (TileClassification.Walkable or TileClassification.Discouraged))
                    continue;
                var nextCost = queuedCost + 10
                    + (classification == TileClassification.Discouraged ? 15 : 0);
                if (costs.TryGetValue(next, out var oldCost) && oldCost <= nextCost)
                    continue;
                costs[next] = nextCost;
                frontier.Enqueue(next, nextCost);
            }
        }
        return costs;
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
        // Several semantic entities can legitimately share one tile (for example a
        // shop counter and its interaction service). The grid is only a compact
        // visual projection, so choose the most useful symbol deterministically
        // instead of treating co-located entities as a protocol error.
        var entityTiles = entities
            .GroupBy(entity => entity.Tile)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(entity => GridEntityPriority(entity.Kind))
                    .ThenBy(entity => entity.Kind, StringComparer.Ordinal)
                    .First()
                    .Kind);
        var rows = new List<string>();
        for (var y = minY; y <= maxY; y++)
        {
            var row = new char[maxX - minX + 1];
            for (var x = minX; x <= maxX; x++)
            {
                var tile = new TilePoint(x, y);
                row[x - minX] = tile == player ? '@' : Symbol(grid.Classify(tile));
                if (tile != player && entityTiles.TryGetValue(tile, out var kind))
                    row[x - minX] = kind switch
                    {
                        "crop" => 'c', "planting_tile" => 'p', "debris" => 'r',
                        "container" => 'C', "water_source" => '~', _ => row[x - minX]
                    };
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
                ["c"] = "crop", ["p"] = "planting tile", ["r"] = "debris",
                ["C"] = "container", ["~"] = "water source"
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

    private static int GridEntityPriority(string kind) => kind switch
    {
        "crop" => 0,
        "debris" => 1,
        "planting_tile" => 2,
        "container" => 3,
        "water_source" => 4,
        _ => 5
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

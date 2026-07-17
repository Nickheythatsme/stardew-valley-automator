using Microsoft.Xna.Framework;
using StardewAgent.Core.Navigation;
using StardewAgent.Protocol;
using StardewValley;
using StardewValley.TerrainFeatures;
using xTile.Dimensions;

namespace StardewAgent.Mod.Navigation;

internal sealed class GameNavigationGrid : INavigationGrid
{
    private readonly GameLocation location;
    private readonly Farmer player;

    public GameNavigationGrid(GameLocation location, Farmer player)
    {
        this.location = location;
        this.player = player;
        Width = location.Map.Layers[0].LayerWidth;
        Height = location.Map.Layers[0].LayerHeight;
    }

    public int Width { get; }
    public int Height { get; }

    public TileClassification Classify(TilePoint tile)
    {
        if (tile.X < 0 || tile.Y < 0 || tile.X >= Width || tile.Y >= Height)
            return TileClassification.StaticBlocked;
        if (location.isWaterTile(tile.X, tile.Y))
            return TileClassification.StaticBlocked;
        if (location.Objects.ContainsKey(new Vector2(tile.X, tile.Y)))
            return TileClassification.StaticBlocked;
        if (location.terrainFeatures.TryGetValue(new Vector2(tile.X, tile.Y), out var feature))
        {
            if (feature is HoeDirt dirt)
            {
                if (!dirt.isPassable(player))
                    return TileClassification.StaticBlocked;
                return TileClassification.Discouraged;
            }
            if (!feature.isPassable(player))
                return TileClassification.StaticBlocked;
        }
        if (location.largeTerrainFeatures.Any(feature => feature.getBoundingBox().Intersects(
                new Microsoft.Xna.Framework.Rectangle(tile.X * Game1.tileSize, tile.Y * Game1.tileSize, Game1.tileSize, Game1.tileSize))))
            return TileClassification.StaticBlocked;
        if (location.characters.Any(character => character.TilePoint.X == tile.X && character.TilePoint.Y == tile.Y))
            return TileClassification.DynamicBlocked;
        if (!location.isTilePassable(new Location(tile.X, tile.Y), Game1.viewport))
            return TileClassification.StaticBlocked;
        if (location.warps.Any(warp => warp.X == tile.X && warp.Y == tile.Y))
            return TileClassification.Warp;
        return TileClassification.Walkable;
    }
}

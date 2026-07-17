using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;

namespace StardewAgent.Mod.Game;

internal sealed class GameFacade : IGameFacade
{
    private static readonly HashSet<string> SupportedLocations = new(StringComparer.Ordinal)
    {
        "Farm", "FarmHouse", "BusStop", "Town", "SeedShop"
    };
    public GameFacade(IModHelper helper)
    {
        ArgumentNullException.ThrowIfNull(helper);
    }

    public bool WorldReady => Context.IsWorldReady;
    public Farmer Player => Game1.player;
    public GameLocation Location => Game1.currentLocation;
    public long Tick => Game1.ticks;
    public int TimeOfDay => Game1.timeOfDay;
    public string LocationName => WorldReady ? Location.NameOrUniqueName : string.Empty;
    public bool IsSinglePlayer => !Context.IsMultiplayer;
    public bool IsFarm => WorldReady && LocationName == "Farm";
    public bool IsSupportedLocation => WorldReady && SupportedLocations.Contains(LocationName);
    public bool InputSuspended =>
        WorldReady && Game1.options.pauseWhenOutOfFocus && !Game1.game1.IsActive;
    public bool IsGameWindowActive => Game1.game1.IsActive;

    public WateringCan? FindWateringCan(out int slot)
    {
        for (var index = 0; index < Player.Items.Count; index++)
        {
            if (Player.Items[index] is WateringCan wateringCan)
            {
                slot = index;
                return wateringCan;
            }
        }
        slot = -1;
        return null;
    }

    public HoeDirt? GetDirt(Point tile) =>
        Location.GetHoeDirtAtTile(new Vector2(tile.X, tile.Y));

    public void Face(int direction) => Player.faceDirection(direction);

    public void SelectTool(int slot) => Player.CurrentToolIndex = slot;
}

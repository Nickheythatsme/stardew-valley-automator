using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;

namespace StardewAgent.Mod.Game;

internal interface IGameFacade
{
    bool WorldReady { get; }
    Farmer Player { get; }
    GameLocation Location { get; }
    long Tick { get; }
    int TimeOfDay { get; }
    string LocationName { get; }
    bool IsSinglePlayer { get; }
    bool IsFarm { get; }
    WateringCan? FindWateringCan(out int slot);
    HoeDirt? GetDirt(Point tile);
    void Face(int direction);
    void SelectTool(int slot);
}

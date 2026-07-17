using System.Text.Json;
using StardewAgent.Mod.Execution;
using StardewAgent.Mod.Game;
using StardewAgent.Mod.Observation;
using StardewAgent.Mod.Protocol;
using StardewAgent.Protocol;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.TerrainFeatures;

namespace StardewAgent.Mod;

public sealed class ModEntry : StardewModdingAPI.Mod
{
    private ProtocolServer? protocol;
    private ObservationBuilder? observations;
    private PlanExecutor? executor;
    private IGameFacade? game;

    public override void Entry(IModHelper helper)
    {
        game = new GameFacade(helper);
        observations = new ObservationBuilder(game);
        protocol = new ProtocolServer(Monitor, Path.Combine(helper.DirectoryPath, ".runtime", "endpoint.json"));
        executor = new PlanExecutor(game, helper, observations, protocol);

        helper.Events.GameLoop.UpdateTicking += OnUpdateTicking;
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        helper.Events.GameLoop.DayStarted += OnDayStarted;
        helper.Events.GameLoop.TimeChanged += OnWorldChanged;
        helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;
        helper.Events.Player.Warped += OnWarped;
        helper.Events.Player.InventoryChanged += OnWorldChanged;
        helper.Events.World.ObjectListChanged += OnWorldChanged;
        helper.Events.World.TerrainFeatureListChanged += OnWorldChanged;
        helper.Events.World.ChestInventoryChanged += OnWorldChanged;
        helper.Events.Display.MenuChanged += OnMenuChanged;
        helper.Events.Input.ButtonPressed += OnButtonPressed;
        helper.ConsoleCommands.Add("stardew_agent_probe", "Report read-only compatibility data for the Stardew Agent mod.", Probe);
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        protocol.Start();
    }

    private void OnUpdateTicking(object? sender, UpdateTickingEventArgs e) => executor?.ApplyInput();

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (protocol is null || observations is null || executor is null)
            return;
        for (var count = 0; count < 8 && protocol.TryDequeue(out var command); count++)
        {
            if (command is not null)
                Handle(command.Request);
        }
        executor.Tick();
        if (executor.IsRunning && !protocol.IsClientConnected)
            executor.CancelActive("GAME_INTERRUPTED", "The supervising client disconnected.");
    }

    private void Handle(RequestEnvelope request)
    {
        if (protocol is null || observations is null || executor is null)
            return;
        try
        {
            switch (request.Method)
            {
                case "hello":
                    protocol.SendResponse(ResponseEnvelope.Success(request.RequestId, new HelloResult(
                        ProtocolLimits.ProtocolVersion,
                        ProtocolLimits.SchemaVersion,
                        ModManifest.Version.ToString(),
                        Game1.version,
                        Constants.ApiVersion.ToString(),
                        new[]
                        {
                            "observe", "travel_to", "clear_debris", "plant_crop", "water_crop",
                            "refill_watering_can", "harvest_crop", "buy_item", "ship_items",
                            "wait_until", "sleep",
                            "advance_dialogue", "dismiss_menu", "finish"
                        },
                        $"game-{Environment.ProcessId}")));
                    break;
                case "ping":
                    protocol.SendResponse(ResponseEnvelope.Success(request.RequestId, new { pong = true }));
                    break;
                case "observe":
                    var observe = request.Params.Deserialize<ObserveParams>(JsonDefaults.Options) ?? new ObserveParams();
                    protocol.SendResponse(ResponseEnvelope.Success(request.RequestId, observations.Build(observe.GridRadius ?? 10)));
                    break;
                case "execute_plan":
                    var execute = request.Params.Deserialize<ExecutePlanParams>(JsonDefaults.Options)
                        ?? throw new GameStateException("INVALID_PARAMS", "execute_plan requires a plan.");
                    var encoded = JsonSerializer.SerializeToUtf8Bytes(execute.Plan, JsonDefaults.Options);
                    if (encoded.Length > ProtocolLimits.MaxPlanBytes)
                        throw new GameStateException("PLAN_TOO_LARGE", "Plans are limited to 32 KiB.");
                    var executionId = executor.Start(execute.Plan);
                    protocol.SendResponse(ResponseEnvelope.Success(request.RequestId, new { execution_id = executionId }));
                    break;
                case "get_execution":
                    var get = request.Params.Deserialize<ExecutionIdParams>(JsonDefaults.Options)
                        ?? throw new GameStateException("INVALID_PARAMS", "get_execution requires execution_id.");
                    protocol.SendResponse(ResponseEnvelope.Success(request.RequestId, executor.Get(get.ExecutionId)));
                    break;
                case "cancel_execution":
                    var cancel = request.Params.Deserialize<ExecutionIdParams>(JsonDefaults.Options)
                        ?? throw new GameStateException("INVALID_PARAMS", "cancel_execution requires execution_id.");
                    executor.Cancel(cancel.ExecutionId);
                    protocol.SendResponse(ResponseEnvelope.Success(request.RequestId, new { cancelled = true }));
                    break;
                default:
                    protocol.SendResponse(ResponseEnvelope.Failure(request.RequestId, "UNKNOWN_METHOD", $"Unknown method '{request.Method}'."));
                    break;
            }
        }
        catch (PlanRejectedException error)
        {
            protocol.SendResponse(ResponseEnvelope.Failure(request.RequestId, "VALIDATION_FAILED", error.Message, error.Validation.Issues));
        }
        catch (GameStateException error)
        {
            protocol.SendResponse(ResponseEnvelope.Failure(request.RequestId, error.Code, error.Message));
        }
        catch (JsonException error)
        {
            protocol.SendResponse(ResponseEnvelope.Failure(request.RequestId, "INVALID_PARAMS", error.Message));
        }
        catch (Exception error)
        {
            Monitor.Log($"Request {request.RequestId} failed:\n{error}", LogLevel.Error);
            protocol.SendResponse(ResponseEnvelope.Failure(request.RequestId, "INTERNAL_ERROR", "The mod could not complete the request."));
        }
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        observations?.Invalidate();
        if (game is null)
            return;
        Monitor.Log(
            $"Compatibility: game={Game1.version}, SMAPI={Constants.ApiVersion}, location={game.LocationName}, player={game.Player.TilePoint}.",
            LogLevel.Info);
    }

    private void OnWarped(object? sender, WarpedEventArgs e)
    {
        observations?.Invalidate();
        if (executor?.HandleWarp(e.OldLocation.NameOrUniqueName, e.NewLocation.NameOrUniqueName) == true)
            return;
        executor?.CancelActive("GAME_INTERRUPTED", $"Location changed from {e.OldLocation.NameOrUniqueName} to {e.NewLocation.NameOrUniqueName}.");
        protocol?.Publish("game_interrupted", null, new { code = "LOCATION_CHANGED" });
    }

    private void OnMenuChanged(object? sender, MenuChangedEventArgs e)
    {
        observations?.Invalidate();
        if (executor?.IsRunning == true
            && executor.HandleMenuChanged(e.OldMenu, e.NewMenu) == false
            && e.NewMenu is not null)
            executor.CancelActive("GAME_INTERRUPTED", $"Unexpected menu opened: {e.NewMenu.GetType().Name}.");
    }

    private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (executor?.IsRunning != true)
            return;
        if (executor.InjectedInputActive)
            return;
        if (e.Button.IsActionButton() || e.Button.IsUseToolButton() || IsMovementButton(e.Button))
            executor.CancelActive("GAME_INTERRUPTED", $"Manual input interrupted automation: {e.Button}.");
    }

    private void OnWorldChanged(object? sender, EventArgs e) => observations?.Invalidate();

    private void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        observations?.Invalidate();
        executor?.HandleDayStarted();
    }

    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
    {
        observations?.Invalidate();
        executor?.CancelActive("GAME_INTERRUPTED", "The game returned to the title screen.");
    }

    private void Probe(string command, string[] args)
    {
        if (game is null || observations is null || !game.WorldReady)
        {
            Monitor.Log("Load a save before running stardew_agent_probe.", LogLevel.Warn);
            return;
        }
        try
        {
            var observation = observations.Build(5);
            var can = game.FindWateringCan(out var slot);
            Monitor.Log(
                $"game={Game1.version}; smapi={Constants.ApiVersion}; location={observation.Game.Location}; " +
                $"player={observation.Player.Tile.X},{observation.Player.Tile.Y}; inventory={observation.Inventory.Count}; " +
                $"crops={observation.Entities.Count(entity => entity.Kind == "crop")}; dry={observation.Summary.DryCrops}; " +
                $"watering_can_slot={slot}; water={can?.WaterLeft.ToString() ?? "missing"}; grid={observation.LocalGrid.Width}x{observation.LocalGrid.Height}",
                LogLevel.Info);
        }
        catch (GameStateException error)
        {
            Monitor.Log($"Probe unavailable: {error.Code}: {error.Message}", LogLevel.Warn);
        }
    }

    private void OnProcessExit(object? sender, EventArgs e) => protocol?.Dispose();

    private static bool IsMovementButton(SButton button) =>
        Game1.options.moveUpButton.Any(value => value.ToSButton() == button)
        || Game1.options.moveRightButton.Any(value => value.ToSButton() == button)
        || Game1.options.moveDownButton.Any(value => value.ToSButton() == button)
        || Game1.options.moveLeftButton.Any(value => value.ToSButton() == button);
}

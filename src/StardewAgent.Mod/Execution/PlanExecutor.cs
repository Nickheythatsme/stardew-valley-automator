using System.Text.Json;
using Microsoft.Xna.Framework;
using StardewAgent.Core.Execution;
using StardewAgent.Mod.Game;
using StardewAgent.Mod.Navigation;
using StardewAgent.Mod.Observation;
using StardewAgent.Mod.Protocol;
using StardewAgent.Protocol;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;
using ProtocolObservation = StardewAgent.Protocol.Observation;

namespace StardewAgent.Mod.Execution;

internal sealed class PlanExecutor
{
    private readonly IGameFacade game;
    private readonly IModHelper helper;
    private readonly ObservationBuilder observations;
    private readonly ProtocolServer protocol;
    private readonly MovementController movement;
    private readonly Dictionary<string, MutableExecution> executions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> planIds = new(StringComparer.Ordinal);
    private MutableExecution? active;
    private ActiveAction? currentAction;
    private bool cancelRequested;
    private long lastExecutionGameTick = long.MinValue / 2;
    private long lastInputGameTick = long.MinValue / 2;

    public PlanExecutor(IGameFacade game, IModHelper helper, ObservationBuilder observations, ProtocolServer protocol)
    {
        this.game = game;
        this.helper = helper;
        this.observations = observations;
        this.protocol = protocol;
        movement = new(game);
    }

    public bool IsRunning => active is not null;
    public SButton? LastInjectedButton { get; private set; }
    public bool InjectedInputActive => game.Tick - lastInjectedTick <= 2;
    private long lastInjectedTick = long.MinValue / 2;

    public string Start(ActionPlan plan)
    {
        if (planIds.TryGetValue(plan.PlanId, out var existing))
            return existing;
        if (active is not null)
            throw new GameStateException("EXECUTION_IN_PROGRESS", "Only one plan may execute at a time.");
        var validation = PlanValidation.Validate(plan, observations.CurrentObservationId ?? string.Empty);
        if (!validation.Valid)
            throw new PlanRejectedException(validation);

        var executionId = $"exec-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        active = new MutableExecution(executionId, plan, now);
        executions[executionId] = active;
        planIds[plan.PlanId] = executionId;
        protocol.Publish("execution_state_changed", executionId, new { status = "ACCEPTED" });
        return executionId;
    }

    public ExecutionResult Get(string executionId)
    {
        if (!executions.TryGetValue(executionId, out var execution))
            throw new GameStateException("EXECUTION_NOT_FOUND", "The requested execution ID is unknown.");
        return execution.Snapshot();
    }

    public void Cancel(string executionId)
    {
        if (!executions.TryGetValue(executionId, out var execution))
            throw new GameStateException("EXECUTION_NOT_FOUND", "The requested execution ID is unknown.");
        if (ReferenceEquals(execution, active))
            cancelRequested = true;
    }

    public void CancelActive(string code, string message)
    {
        if (active is not null)
            FinishExecution(code, message, true);
    }

    public bool HandleWarp(string oldLocation, string newLocation)
    {
        if (currentAction?.PlanAction.Action != "travel_to"
            || currentAction.ExpectedFromLocation != oldLocation
            || currentAction.ExpectedToLocation != newLocation)
            return false;
        movement.Cancel("EXPECTED_WARP");
        Succeed("OK", $"Travelled from {oldLocation} to {newLocation}.", true);
        observations.Invalidate();
        return true;
    }

    public bool HandleMenuChanged(IClickableMenu? oldMenu, IClickableMenu? newMenu)
    {
        if (currentAction is null)
            return false;
        if (currentAction.PlanAction.Action == "sleep"
            && (newMenu?.GetType().Name.Contains("Dialogue", StringComparison.OrdinalIgnoreCase) == true
                || newMenu is null))
        {
            currentAction.OwnsMenu = newMenu is not null;
            return true;
        }
        if (currentAction.PlanAction.Action is "advance_dialogue" or "dismiss_menu")
        {
            currentAction.OwnsMenu = newMenu is not null;
            return true;
        }
        if (currentAction.PlanAction.Action is "buy_item" or "ship_items")
        {
            currentAction.OwnsMenu = newMenu is not null;
            return newMenu is null
                || newMenu is ShopMenu
                || newMenu.GetType().Name.Contains("Shipping", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    public void HandleDayStarted()
    {
        observations.Invalidate();
        if (currentAction?.PlanAction.Action == "sleep")
            Succeed("OK", "The day advanced and the new day started.", true);
    }

    public void ApplyInput()
    {
        LastInjectedButton = null;
        if (active is null || currentAction is null)
            return;
        if (game.InputSuspended)
            return;
        if (game.Tick == lastInputGameTick)
            return;
        lastInputGameTick = game.Tick;
        if (movement.Running)
        {
            var button = movement.InputForCurrentTick();
            if (button is not null)
            {
                LastInjectedButton = button;
                helper.Input.Press(button.Value);
                lastInjectedTick = game.Tick;
            }
            return;
        }
        if (currentAction.Phase == ActionPhase.TriggerInput && !currentAction.InputTriggered)
        {
            var button = currentAction.UseActionButton
                ? (Game1.options.actionButton.Count() > 0
                    ? Game1.options.actionButton[0].ToSButton()
                    : SButton.MouseRight)
                : (Game1.options.useToolButton.Count() > 0
                    ? Game1.options.useToolButton[0].ToSButton()
                    : SButton.MouseLeft);
            LastInjectedButton = button;
            helper.Input.Press(button);
            lastInjectedTick = game.Tick;
            currentAction.InputTriggered = true;
            currentAction.Phase = ActionPhase.WaitForInput;
            currentAction.Trace.Add(new("TRIGGER_GAME_ACTION", game.Tick));
        }
        else if (currentAction.Phase == ActionPhase.WaitForWarp
                 && currentAction.WarpButton is { } warpButton)
        {
            LastInjectedButton = warpButton;
            helper.Input.Press(warpButton);
            lastInjectedTick = game.Tick;
        }
    }

    public void Tick()
    {
        LastInjectedButton = null;
        if (active is null)
            return;
        if (game.InputSuspended)
            return;
        if (game.Tick == lastExecutionGameTick)
            return;
        lastExecutionGameTick = game.Tick;
        if (cancelRequested)
        {
            FinishExecution("CANCELLED", "Cancellation was requested.", true);
            return;
        }
        if ((!game.WorldReady || !game.IsSupportedLocation)
            && currentAction?.Phase != ActionPhase.WaitForDay)
        {
            FinishExecution("GAME_INTERRUPTED", "The game left the supported crop-runner locations.", true);
            return;
        }
        var exceeded = active.Budget.Exceeded(DateTimeOffset.UtcNow);
        if (exceeded is not null)
        {
            FinishExecution("BUDGET_EXCEEDED", exceeded, true);
            return;
        }
        if (game.Player.Stamina < active.Plan.StopConditions.EnergyBelow || game.TimeOfDay >= active.Plan.StopConditions.GameTimeAtOrAfter)
        {
            FinishExecution("COMPLETED_WITH_FAILURES", "A plan stop condition was reached.", true);
            return;
        }
        if (active.Status == "ACCEPTED")
        {
            active.Status = "RUNNING";
            active.StartedAtUtc = DateTimeOffset.UtcNow;
            protocol.Publish("execution_state_changed", active.ExecutionId, new { status = active.Status });
        }
        if (currentAction is null)
        {
            if (active.ActionIndex >= active.Plan.Actions.Count)
            {
                FinishExecution(active.Results.Any(result => result.Status == "failed") ? "COMPLETED_WITH_FAILURES" : "COMPLETED", "The action queue completed.", false);
                return;
            }
            Begin(active.Plan.Actions[active.ActionIndex]);
        }
        TickCurrentAction();
    }

    private void Begin(PlanAction action)
    {
        if (active is null)
            return;
        active.Budget.RecordAction();
        currentAction = new ActiveAction(action, DateTimeOffset.UtcNow, game.TimeOfDay);
        currentAction.BaseObservationId = observations.CurrentObservationId ?? string.Empty;
        currentAction.Trace.Add(new("CHECK_PRECONDITIONS", game.Tick));
        switch (action.Action)
        {
            case "finish":
                Succeed("FINISHED", action.Args.GetProperty("reason").GetString() ?? "Finished.", false);
                FinishExecution("COMPLETED", "The plan finished explicitly.", false);
                break;
            case "wait_until":
                currentAction.TargetTime = action.Args.GetProperty("time").GetInt32();
                currentAction.Phase = ActionPhase.Wait;
                break;
            case "water_crop":
                BeginWaterCrop();
                break;
            case "refill_watering_can":
                BeginRefillWateringCan();
                break;
            case "harvest_crop":
                BeginHarvestCrop();
                break;
            case "plant_crop":
                BeginPlantCrop();
                break;
            case "clear_debris":
                BeginClearDebris();
                break;
            case "travel_to":
                BeginTravel();
                break;
            case "sleep":
                BeginSleep();
                break;
            case "advance_dialogue":
                BeginAdvanceDialogue();
                break;
            case "dismiss_menu":
                if (Game1.activeClickableMenu is null)
                    Succeed("ALREADY_SATISFIED", "No menu is open.", false);
                else
                {
                    Game1.exitActiveMenu();
                    Succeed("OK", "The active vanilla menu was dismissed.", true);
                }
                break;
            case "buy_item":
                BeginBuyItem();
                break;
            case "ship_items":
                BeginShipItems();
                break;
            default:
                Fail("ACTION_NOT_IMPLEMENTED", $"Action '{action.Action}' is reserved by the schema but is not enabled in this prototype.", false);
                break;
        }
    }

    private void BeginShipItems()
    {
        if (currentAction is null || observations.LastObservation is null)
            return;
        var selectedIds = currentAction.PlanAction.Args.TryGetProperty("selector_item_ids", out var ids)
            ? ids.EnumerateArray().Select(value => value.GetString()).Where(value => value is not null).ToHashSet(StringComparer.Ordinal)
            : null;
        var category = currentAction.PlanAction.Args.TryGetProperty("category", out var categoryValue)
            ? categoryValue.GetString()
            : null;
        var slots = new List<int>();
        for (var slot = 0; slot < game.Player.Items.Count; slot++)
        {
            var item = game.Player.Items[slot];
            if (item is null || item is Tool || item is not StardewValley.Object || item.sellToStorePrice() <= 0)
                continue;
            var selected = selectedIds?.Contains(item.QualifiedItemId) == true
                || category == "crop" && (item.HasContextTag("category_vegetable")
                    || item.HasContextTag("category_fruit")
                    || item.HasContextTag("category_flower"))
                || category == "seed" && item.Category == StardewValley.Object.SeedsCategory
                || category == "forage" && item.HasContextTag("category_forage");
            if (selected)
                slots.Add(slot);
        }
        currentAction.Preconditions.Add(new("matching_items_exist", slots.Count > 0));
        if (slots.Count == 0)
        {
            Fail("ITEM_NOT_FOUND", "No shippable inventory items matched the selector.", false);
            return;
        }
        currentAction.InventorySlots = slots;
        currentAction.InventoryBefore = slots.Sum(slot => game.Player.Items[slot]?.Stack ?? 0);
        currentAction.ShippingBefore = ShippingQuantity();
        if (Game1.activeClickableMenu?.GetType().Name.Contains("Shipping", StringComparison.OrdinalIgnoreCase) == true)
        {
            currentAction.Phase = ActionPhase.PerformMenuAction;
            return;
        }
        var bin = observations.LastObservation.Entities
            .Where(entity => entity.Kind == "shipping_bin" && entity.Reachable)
            .OrderBy(entity => entity.PathCost ?? int.MaxValue)
            .FirstOrDefault();
        if (bin is null)
        {
            Fail("SHIPPING_BIN_UNREACHABLE", "No reachable shipping bin action is available.", true);
            return;
        }
        currentAction.TargetId = bin.Id;
        currentAction.TargetTile = bin.Tile;
        currentAction.Operation = "open_shipping";
        StartEntityMovement(bin);
    }

    private void BeginBuyItem()
    {
        if (currentAction is null || observations.LastObservation is null)
            return;
        if (Game1.activeClickableMenu is not ShopMenu shop)
        {
            Fail("WRONG_MENU", "buy_item requires the observed vanilla shop menu to remain open.", true);
            return;
        }
        var offerId = currentAction.PlanAction.Args.GetProperty("offer_id").GetString()!;
        var revision = currentAction.PlanAction.Args.GetProperty("offer_revision").GetString()!;
        var quantity = currentAction.PlanAction.Args.GetProperty("quantity").GetInt32();
        var offer = observations.LastObservation.ShopOffers.FirstOrDefault(candidate => candidate.Id == offerId);
        currentAction.Preconditions.Add(new("offer_exists", offer is not null));
        if (offer is null || offer.Revision != revision)
        {
            Fail("OFFER_STALE", "The selected shop offer changed.", false);
            return;
        }
        var saleIndex = shop.forSale.FindIndex(sale =>
            sale is Item item && item.QualifiedItemId == offer.QualifiedItemId);
        if (saleIndex is < 0 or > 3)
        {
            Fail("OFFER_NOT_VISIBLE", "The selected offer is not in the visible bounded shop rows.", true);
            return;
        }
        var totalPrice = checked(offer.Price * quantity);
        currentAction.Preconditions.Add(new("sufficient_money", game.Player.Money >= totalPrice));
        if (game.Player.Money < totalPrice)
        {
            Fail("INSUFFICIENT_MONEY", "The purchase exceeds the current wallet.", false);
            return;
        }
        currentAction.OfferItemId = offer.QualifiedItemId;
        currentAction.MenuIndex = saleIndex;
        currentAction.Quantity = quantity;
        currentAction.MoneyBefore = game.Player.Money;
        currentAction.InventoryBefore = InventoryQuantity(offer.QualifiedItemId);
        currentAction.Phase = ActionPhase.PerformMenuAction;
    }

    private void PerformBuyItem()
    {
        if (currentAction is null || Game1.activeClickableMenu is not ShopMenu shop)
        {
            Fail("WRONG_MENU", "The observed shop menu closed before purchase.", true);
            return;
        }
        currentAction.ToolWaitTicks++;
        if (currentAction.ToolWaitTicks < 30)
            return;
        var x = shop.xPositionOnScreen + 160;
        var y = shop.yPositionOnScreen + 120 + currentAction.MenuIndex * 98;
        for (var count = 0; count < currentAction.Quantity; count++)
            shop.receiveLeftClick(x, y);
        var inventoryAfter = InventoryQuantity(currentAction.OfferItemId!);
        if (inventoryAfter - currentAction.InventoryBefore == currentAction.Quantity
            && game.Player.Money < currentAction.MoneyBefore)
        {
            Succeed("OK", "The requested item quantity increased and wallet money decreased.", true);
            return;
        }
        Fail("PURCHASE_NOT_CONFIRMED", "The exact requested purchase was not verified.", false);
    }

    private void BeginSleep()
    {
        if (currentAction is null || observations.LastObservation is null)
            return;
        var bed = observations.LastObservation.Entities
            .Where(entity => entity.Kind == "bed" && entity.Reachable)
            .OrderBy(entity => entity.PathCost ?? int.MaxValue)
            .FirstOrDefault();
        currentAction.Preconditions.Add(new("bed_exists", bed is not null));
        if (bed is null)
        {
            Fail("BED_NOT_REACHABLE", "No reachable bed action is available.", true);
            return;
        }
        currentAction.TargetId = bed.Id;
        currentAction.TargetTile = bed.Tile;
        currentAction.Operation = "start_sleep";
        currentAction.StartDay = Game1.Date.TotalDays;
        StartEntityMovement(bed);
    }

    private void BeginAdvanceDialogue()
    {
        if (currentAction is null)
            return;
        if (Game1.activeClickableMenu is not null)
        {
            currentAction.UseActionButton = true;
            currentAction.Operation = "advance_menu";
            currentAction.Phase = ActionPhase.TriggerInput;
            return;
        }
        var shop = observations.LastObservation?.Entities
            .Where(entity => entity.Kind == "shop" && entity.Reachable)
            .OrderBy(entity => entity.PathCost ?? int.MaxValue)
            .FirstOrDefault();
        if (shop is null)
        {
            Fail("NO_DIALOGUE_TARGET", "There is no open dialogue or reachable vanilla service counter.", true);
            return;
        }
        currentAction.TargetId = shop.Id;
        currentAction.TargetTile = shop.Tile;
        currentAction.Operation = "open_shop";
        StartEntityMovement(shop);
    }

    private void BeginTravel()
    {
        if (currentAction is null || observations.LastObservation is null)
            return;
        var destination = currentAction.PlanAction.Args.GetProperty("destination").GetString()!;
        var route = observations.LastObservation.Routes
            .Where(edge => edge.FromLocation == game.LocationName && edge.ToLocation == destination)
            .OrderBy(edge => edge.DepartureTile.Y)
            .ThenBy(edge => edge.DepartureTile.X)
            .FirstOrDefault();
        currentAction.Preconditions.Add(new("direct_route_exists", route is not null));
        if (route is null)
        {
            Fail("NO_ROUTE", $"No direct route from {game.LocationName} to {destination} is currently available.", true);
            return;
        }
        currentAction.ExpectedFromLocation = route.FromLocation;
        currentAction.ExpectedToLocation = route.ToLocation;
        currentAction.TargetTile = route.DepartureTile;
        currentAction.WarpButton = WarpButton(route.DepartureTile);
        currentAction.Trace.Add(new("MOVE_TO_EXPECTED_WARP", game.Tick, $"{route.DepartureTile.X},{route.DepartureTile.Y}"));
        if (!movement.Start(route.DepartureTile, allowWarp: true))
        {
            Fail(movement.FailureCode ?? "NO_PATH", "No path to the expected warp was found.", true);
            return;
        }
        currentAction.Phase = ActionPhase.Move;
    }

    private void BeginHarvestCrop()
    {
        if (!ResolveEntity("crop", out var entity))
            return;
        var dirt = game.GetDirt(new Point(entity!.Tile.X, entity.Tile.Y));
        var harvestable = dirt?.crop is { } crop
            && crop.currentPhase.Value >= crop.phaseDays.Count - 1
            && (!crop.fullyGrown.Value || crop.dayOfCurrentPhase.Value <= 0);
        currentAction!.Preconditions.Add(new("target_is_harvestable", harvestable));
        if (!harvestable)
        {
            Fail("NOT_HARVESTABLE", "The crop is not ready for hand harvesting.", false);
            return;
        }
        currentAction.TargetTile = entity.Tile;
        currentAction.InventoryBefore = InventoryQuantity();
        StartEntityMovement(entity);
    }

    private void BeginPlantCrop()
    {
        if (!ResolveEntity("planting_tile", out var entity))
            return;
        var qualifiedItemId = currentAction!.PlanAction.Args.GetProperty("qualified_item_id").GetString()!;
        var slot = FindInventoryItem(qualifiedItemId);
        currentAction.Preconditions.Add(new("seed_exists", slot >= 0));
        if (slot < 0)
        {
            Fail("ITEM_NOT_FOUND", "The requested seed is not in inventory.", false);
            return;
        }
        var item = game.Player.Items[slot];
        currentAction.Preconditions.Add(new("item_is_seed", item?.Category == StardewValley.Object.SeedsCategory));
        if (item?.Category != StardewValley.Object.SeedsCategory)
        {
            Fail("INVALID_SEED", "The selected inventory item is not a seed.", false);
            return;
        }
        var dirt = game.GetDirt(new Point(entity!.Tile.X, entity.Tile.Y));
        if (dirt?.crop is not null)
        {
            Fail("SOIL_OCCUPIED", "The planting tile already contains a crop.", false);
            return;
        }
        currentAction.TargetTile = entity.Tile;
        currentAction.SeedSlot = slot;
        currentAction.InventoryBefore = item.Stack;
        currentAction.WaterAfterPlanting =
            currentAction.PlanAction.Args.GetProperty("water_after_planting").GetBoolean();
        if (dirt is null)
        {
            var hoeSlot = FindTool<Hoe>();
            currentAction.Preconditions.Add(new("hoe_exists", hoeSlot >= 0));
            if (hoeSlot < 0)
            {
                Fail("NO_HOE", "A hoe is required to prepare this planting tile.", false);
                return;
            }
            currentAction.ToolSlot = hoeSlot;
            currentAction.Operation = "till";
        }
        else
        {
            currentAction.ToolSlot = slot;
            currentAction.Operation = "plant";
        }
        StartEntityMovement(entity);
    }

    private void BeginClearDebris()
    {
        if (!ResolveEntity("debris", out var entity))
            return;
        var requiredTool = entity!.Properties.TryGetValue("required_tool", out var value)
            ? value?.ToString()
            : null;
        var slot = requiredTool switch
        {
            "axe" => FindTool<Axe>(),
            "pickaxe" => FindTool<Pickaxe>(),
            "scythe" => FindTool<MeleeWeapon>(),
            _ => -1
        };
        currentAction!.Preconditions.Add(new("required_tool_exists", slot >= 0));
        if (slot < 0)
        {
            Fail("TOOL_NOT_FOUND", $"No {requiredTool ?? "supported"} tool was found.", false);
            return;
        }
        currentAction.TargetTile = entity.Tile;
        currentAction.ToolSlot = slot;
        currentAction.Operation = "clear";
        StartEntityMovement(entity);
    }

    private bool ResolveEntity(string kind, out Entity? entity)
    {
        entity = null;
        if (currentAction is null || observations.LastObservation is null)
            return false;
        var targetId = currentAction.PlanAction.Args.GetProperty("target_id").GetString()!;
        var revision = currentAction.PlanAction.Args.GetProperty("target_revision").GetString()!;
        currentAction.TargetId = targetId;
        entity = observations.LastObservation.Entities.FirstOrDefault(candidate => candidate.Id == targetId);
        currentAction.Preconditions.Add(new("target_exists", entity is not null));
        if (entity is null || entity.Kind != kind || entity.Location != game.LocationName)
        {
            Fail("TARGET_STALE", $"The {kind} target is no longer present in the current location.", false);
            return false;
        }
        currentAction.Preconditions.Add(new("target_revision_matches", entity.Revision == revision));
        if (entity.Revision != revision)
        {
            Fail("TARGET_STALE", "The target revision changed.", false);
            return false;
        }
        return true;
    }

    private void StartEntityMovement(Entity entity)
    {
        currentAction!.InteractionOptions = entity.InteractionTiles
            .Where(tile => tile.Reachable)
            .OrderBy(tile => tile.PathCost ?? int.MaxValue)
            .ThenBy(tile => tile.Tile.Y)
            .ThenBy(tile => tile.Tile.X)
            .ToArray();
        currentAction.InteractionIndex = -1;
        if (!StartNextInteractionPosition())
        {
            Fail("TARGET_UNREACHABLE", "No reachable interaction tile exists.", true);
        }
    }

    private bool TryNextInteractionPosition(string failureCode)
    {
        if (failureCode is not ("STUCK" or "NO_PATH" or "TARGET_UNREACHABLE"))
            return false;
        return StartNextInteractionPosition();
    }

    private bool StartNextInteractionPosition()
    {
        if (currentAction is null)
            return false;
        while (++currentAction.InteractionIndex < currentAction.InteractionOptions.Count)
        {
            var interaction = currentAction.InteractionOptions[currentAction.InteractionIndex];
            currentAction.Facing = interaction.Facing;
            currentAction.Trace.Add(new(
                "TRY_INTERACTION_POSITION",
                game.Tick,
                $"{interaction.Tile.X},{interaction.Tile.Y}"));
            if (movement.Start(interaction.Tile))
            {
                currentAction.Phase = ActionPhase.Move;
                return true;
            }
        }
        return false;
    }

    private void BeginWaterCrop()
    {
        if (currentAction is null || observations.LastObservation is null)
            return;
        var targetId = currentAction.PlanAction.Args.GetProperty("target_id").GetString()!;
        var targetRevision = currentAction.PlanAction.Args.GetProperty("target_revision").GetString()!;
        currentAction.TargetId = targetId;
        var entity = observations.LastObservation.Entities.FirstOrDefault(entity => entity.Id == targetId);
        currentAction.Preconditions.Add(new("target_exists", entity is not null));
        if (entity is null || entity.Kind != "crop")
        {
            Fail("TARGET_STALE", "The crop target no longer exists.", false);
            return;
        }
        currentAction.Preconditions.Add(new("target_revision_matches", entity.Revision == targetRevision));
        if (entity.Revision != targetRevision)
        {
            Fail("TARGET_STALE", "The crop revision changed.", false);
            return;
        }
        var dirt = game.GetDirt(new Point(entity.Tile.X, entity.Tile.Y));
        if (dirt is null || dirt.crop is null)
        {
            Fail("TARGET_STALE", "The crop target is no longer present.", false);
            return;
        }
        currentAction.Preconditions.Add(new("target_is_dry_crop", !dirt.isWatered() && dirt.needsWatering()));
        if (dirt.isWatered() || !dirt.needsWatering())
        {
            Fail("ALREADY_SATISFIED", "The crop does not need watering.", true);
            return;
        }
        var wateringCan = game.FindWateringCan(out var slot);
        currentAction.Preconditions.Add(new("watering_can_exists", wateringCan is not null));
        if (wateringCan is null)
        {
            Fail("NO_WATERING_CAN", "No watering can was found in inventory.", false);
            return;
        }
        currentAction.Preconditions.Add(new("watering_can_has_water", wateringCan.WaterLeft > 0));
        if (wateringCan.WaterLeft <= 0)
        {
            Fail("NO_WATER", "The watering can is empty.", true, new RecoverySuggestion("refill_watering_can"));
            return;
        }
        currentAction.ToolSlot = slot;
        currentAction.WaterBefore = wateringCan.WaterLeft;
        currentAction.TargetTile = entity.Tile;
        var interaction = entity.InteractionTiles
            .Where(tile => tile.Reachable)
            .OrderBy(tile => tile.PathCost ?? int.MaxValue)
            .FirstOrDefault();
        if (interaction is null)
        {
            Fail("TARGET_UNREACHABLE", "No reachable crop interaction tile exists.", true);
            return;
        }
        currentAction.Facing = interaction.Facing;
        StartMovement(interaction.Tile);
    }

    private void BeginRefillWateringCan()
    {
        if (currentAction is null || observations.LastObservation is null)
            return;
        var wateringCan = game.FindWateringCan(out var slot);
        currentAction.Preconditions.Add(new("watering_can_exists", wateringCan is not null));
        if (wateringCan is null)
        {
            Fail("NO_WATERING_CAN", "No watering can was found in inventory.", false);
            return;
        }
        currentAction.Preconditions.Add(new("watering_can_not_full", wateringCan.WaterLeft < wateringCan.waterCanMax));
        if (wateringCan.WaterLeft >= wateringCan.waterCanMax)
        {
            Fail("CAN_ALREADY_FULL", "The watering can is already full.", false);
            return;
        }

        var requestedSource = currentAction.PlanAction.Args.TryGetProperty("source_id", out var sourceProperty)
            ? sourceProperty.GetString()
            : null;
        var source = observations.LastObservation.Entities
            .Where(entity => entity.Kind == "water_source" && entity.Reachable)
            .Where(entity => requestedSource is null || entity.Id == requestedSource)
            .OrderBy(entity => entity.PathCost ?? int.MaxValue)
            .ThenBy(entity => entity.Id, StringComparer.Ordinal)
            .FirstOrDefault();
        currentAction.Preconditions.Add(new("water_source_exists", source is not null));
        if (source is null)
        {
            Fail(requestedSource is null ? "NO_WATER_SOURCE" : "TARGET_UNREACHABLE", "No reachable refill source was found.", true);
            return;
        }

        var interaction = source.InteractionTiles
            .Where(tile => tile.Reachable)
            .OrderBy(tile => tile.PathCost ?? int.MaxValue)
            .First();
        currentAction.TargetId = source.Id;
        currentAction.TargetTile = source.Tile;
        currentAction.ToolSlot = slot;
        currentAction.WaterBefore = wateringCan.WaterLeft;
        currentAction.Facing = interaction.Facing;
        StartMovement(interaction.Tile);
    }

    private void StartMovement(TilePoint target)
    {
        if (currentAction is null)
            return;
        currentAction.Trace.Add(new("MOVE_TO_INTERACTION_TILE", game.Tick, $"{target.X},{target.Y}"));
        if (!movement.Start(target))
        {
            Fail(movement.FailureCode ?? "NO_PATH", "No path to the target tile was found.", true);
            return;
        }
        currentAction.Phase = ActionPhase.Move;
    }

    private void TickCurrentAction()
    {
        if (currentAction is null || active is null)
            return;
        switch (currentAction.Phase)
        {
            case ActionPhase.Move:
                movement.Tick();
                if (movement.FailureCode is not null)
                {
                    currentAction.Trace.Add(new("MOVEMENT_DIAGNOSTIC", game.Tick, movement.Diagnostic));
                    if (TryNextInteractionPosition(movement.FailureCode))
                        return;
                    Fail(movement.FailureCode, "Movement failed.", true);
                    return;
                }
                if (!movement.Completed)
                    return;
                for (var i = 0; i < movement.TraversedTiles; i++) active.Budget.RecordMovementTile();
                if (currentAction.PlanAction.Action == "travel_to")
                {
                    currentAction.Phase = ActionPhase.WaitForWarp;
                    currentAction.ToolWaitTicks = 0;
                    return;
                }
                PrepareCurrentAction();
                break;
            case ActionPhase.Wait:
                if (game.TimeOfDay >= currentAction.TargetTime)
                    Succeed("OK", "The requested game time was reached.", false);
                break;
            case ActionPhase.WaitForInput:
                currentAction.ToolWaitTicks++;
                if (game.Player.UsingTool)
                    currentAction.SawToolUse = true;
                if (currentAction.PlanAction.Action == "refill_watering_can")
                {
                    var can = game.FindWateringCan(out _);
                    if (!game.Player.UsingTool && can is not null && can.WaterLeft > currentAction.WaterBefore)
                    {
                        Succeed("OK", "The watering can water level increased.", true);
                        return;
                    }
                    if (currentAction.ToolWaitTicks > 180)
                        Fail("REFILL_NOT_CONFIRMED", "Refilling was not verified before the timeout.", true);
                    return;
                }
                if (currentAction.PlanAction.Action == "harvest_crop")
                {
                    var harvestDirt = DirtForCurrent();
                    if (currentAction.ToolWaitTicks > 2
                        && (harvestDirt?.crop is null || !IsHarvestable(harvestDirt.crop))
                        && InventoryQuantity() > currentAction.InventoryBefore)
                    {
                        Succeed("OK", "The crop was removed and harvest inventory increased.", true);
                        return;
                    }
                    if (currentAction.ToolWaitTicks > 180)
                        Fail("HARVEST_NOT_CONFIRMED", "Harvesting was not verified before the timeout.", false);
                    return;
                }
                if (currentAction.PlanAction.Action == "clear_debris")
                {
                    var tile = currentAction.TargetTile!;
                    if (!game.Location.Objects.ContainsKey(new Vector2(tile.X, tile.Y)))
                    {
                        Succeed("OK", "The debris was removed.", true);
                        return;
                    }
                    if (!game.Player.UsingTool && currentAction.ToolWaitTicks > 15 && currentAction.ToolWaitTicks < 150)
                    {
                        currentAction.InputTriggered = false;
                        currentAction.Phase = ActionPhase.TriggerInput;
                        return;
                    }
                    if (currentAction.ToolWaitTicks > 180)
                        Fail("DEBRIS_NOT_CLEARED", "Debris removal was not verified before the timeout.", true);
                    return;
                }
                if (currentAction.PlanAction.Action == "plant_crop")
                {
                    TickPlantCrop();
                    return;
                }
                if (currentAction.PlanAction.Action == "advance_dialogue")
                {
                    if (currentAction.ToolWaitTicks > 2)
                        Succeed("OK", "The vanilla dialogue/menu action was advanced.", true);
                    return;
                }
                if (currentAction.PlanAction.Action == "sleep")
                {
                    if (Game1.activeClickableMenu is not null && currentAction.Operation == "start_sleep")
                    {
                        currentAction.Operation = "confirm_sleep";
                        currentAction.InputTriggered = false;
                        currentAction.ToolWaitTicks = 0;
                        currentAction.Phase = ActionPhase.TriggerInput;
                        return;
                    }
                    if (currentAction.Operation == "confirm_sleep" && currentAction.ToolWaitTicks > 2)
                    {
                        currentAction.Phase = ActionPhase.WaitForDay;
                        return;
                    }
                    if (currentAction.ToolWaitTicks > 180)
                        Fail("SLEEP_NOT_CONFIRMED", "The sleep transition was not confirmed.", true);
                    return;
                }
                if (currentAction.PlanAction.Action == "ship_items")
                {
                    if (Game1.activeClickableMenu?.GetType().Name.Contains("Shipping", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        currentAction.ToolWaitTicks = 0;
                        currentAction.Phase = ActionPhase.PerformMenuAction;
                        return;
                    }
                    if (currentAction.ToolWaitTicks > 180)
                        Fail("WRONG_MENU", "The shipping menu did not open.", true);
                    return;
                }
                var dirt = currentAction.TargetTile is null
                    ? null
                    : game.GetDirt(new Point(currentAction.TargetTile.X, currentAction.TargetTile.Y));
                if (currentAction.SawToolUse && !game.Player.UsingTool && dirt?.isWatered() == true)
                {
                    Succeed("OK", "The crop changed from dry to watered.", true);
                    return;
                }
                if (currentAction.ToolWaitTicks > 180)
                    Fail("TOOL_USE_FAILED", "Watering was not verified before the timeout.", true);
                break;
            case ActionPhase.WaitForWarp:
                currentAction.ToolWaitTicks++;
                if (currentAction.ToolWaitTicks > 120)
                    Fail("LOCATION_CHANGED", "The expected warp did not complete.", true);
                break;
            case ActionPhase.WaitForDay:
                if (game.WorldReady && Game1.Date.TotalDays > currentAction.StartDay)
                    Succeed("OK", "The day advanced.", true);
                break;
            case ActionPhase.PerformMenuAction:
                if (currentAction.PlanAction.Action == "buy_item")
                    PerformBuyItem();
                else if (currentAction.PlanAction.Action == "ship_items")
                    PerformShipItems();
                break;
        }
    }

    private void PerformShipItems()
    {
        if (currentAction is null || Game1.activeClickableMenu is not { } menu
            || !menu.GetType().Name.Contains("Shipping", StringComparison.OrdinalIgnoreCase))
        {
            Fail("WRONG_MENU", "The vanilla shipping menu closed before transfer.", true);
            return;
        }
        currentAction.ToolWaitTicks++;
        if (currentAction.ToolWaitTicks < 20)
            return;
        foreach (var slot in currentAction.InventorySlots.OrderByDescending(value => value))
        {
            var column = slot % 12;
            var row = slot / 12;
            var x = menu.xPositionOnScreen + 32 + column * 64 + 32;
            var y = menu.yPositionOnScreen + menu.height - 224 + row * 64 + 32;
            menu.receiveLeftClick(x, y);
        }
        var remaining = currentAction.InventorySlots.Sum(slot => game.Player.Items[slot]?.Stack ?? 0);
        if (remaining < currentAction.InventoryBefore && ShippingQuantity() > currentAction.ShippingBefore)
        {
            Succeed("OK", "Matching inventory decreased and shipping-bin contents increased.", true);
            return;
        }
        Fail("SHIP_NOT_CONFIRMED", "The selected items were not verified in the shipping bin.", false);
    }

    private void TickPlantCrop()
    {
        if (currentAction is null)
            return;
        var dirt = DirtForCurrent();
        if (currentAction.Operation == "till")
        {
            if (dirt is not null && !game.Player.UsingTool)
            {
                game.SelectTool(currentAction.SeedSlot);
                currentAction.ToolSlot = currentAction.SeedSlot;
                currentAction.Operation = "plant";
                currentAction.UseActionButton = true;
                currentAction.InputTriggered = false;
                currentAction.ToolWaitTicks = 0;
                currentAction.Phase = ActionPhase.TriggerInput;
                return;
            }
            if (currentAction.ToolWaitTicks > 180)
                Fail("TILL_NOT_CONFIRMED", "Tilling was not verified before the timeout.", true);
            return;
        }
        if (currentAction.Operation == "plant")
        {
            var seedRemaining = game.Player.Items[currentAction.SeedSlot]?.Stack ?? 0;
            if (dirt?.crop is not null && seedRemaining < currentAction.InventoryBefore)
            {
                if (!currentAction.WaterAfterPlanting || dirt.isWatered())
                {
                    Succeed("OK", "The seed count decreased and a crop appeared.", true);
                    return;
                }
                var can = game.FindWateringCan(out var canSlot);
                if (can is null || can.WaterLeft <= 0)
                {
                    Fail(can is null ? "NO_WATERING_CAN" : "NO_WATER", "The crop was planted but could not be watered.", true);
                    return;
                }
                game.SelectTool(canSlot);
                currentAction.ToolSlot = canSlot;
                currentAction.Operation = "water_planted";
                currentAction.UseActionButton = false;
                currentAction.InputTriggered = false;
                currentAction.ToolWaitTicks = 0;
                currentAction.Phase = ActionPhase.TriggerInput;
                return;
            }
            if (currentAction.ToolWaitTicks > 90)
                Fail("PLANT_NOT_CONFIRMED", "Planting was not verified before the timeout.", false);
            return;
        }
        if (currentAction.Operation == "water_planted")
        {
            if (currentAction.SawToolUse && !game.Player.UsingTool && dirt?.isWatered() == true)
            {
                Succeed("OK", "The crop was planted and watered.", true);
                return;
            }
            if (currentAction.ToolWaitTicks > 180)
                Fail("TOOL_USE_FAILED", "Watering the planted crop was not verified.", true);
        }
    }

    private void PrepareCurrentAction()
    {
        if (currentAction is null)
            return;
        if (currentAction.PlanAction.Action is "harvest_crop" or "sleep" or "advance_dialogue" or "ship_items")
        {
            game.Face(Direction(currentAction.Facing));
            currentAction.UseActionButton = true;
            currentAction.Phase = ActionPhase.TriggerInput;
            return;
        }
        PrepareTool();
    }

    private void PrepareTool()
    {
        if (currentAction is null || currentAction.ToolSlot is null)
            return;
        game.SelectTool(currentAction.ToolSlot.Value);
        game.Face(Direction(currentAction.Facing));
        currentAction.UseActionButton =
            currentAction.PlanAction.Action == "plant_crop"
            && currentAction.Operation == "plant";
        currentAction.Trace.Add(new("SELECT_TOOL_OR_ITEM", game.Tick));
        currentAction.Trace.Add(new("FACE", game.Tick, currentAction.Facing));
        currentAction.Phase = ActionPhase.TriggerInput;
        active?.Budget.RecordToolUse();
    }

    private int FindInventoryItem(string qualifiedItemId)
    {
        for (var index = 0; index < game.Player.Items.Count; index++)
        {
            if (game.Player.Items[index]?.QualifiedItemId == qualifiedItemId)
                return index;
        }
        return -1;
    }

    private int FindTool<TTool>() where TTool : Tool
    {
        for (var index = 0; index < game.Player.Items.Count; index++)
        {
            if (game.Player.Items[index] is TTool)
                return index;
        }
        return -1;
    }

    private int InventoryQuantity() =>
        game.Player.Items.Where(item => item is not null).Sum(item => item!.Stack);

    private int InventoryQuantity(string qualifiedItemId) =>
        game.Player.Items
            .Where(item => item?.QualifiedItemId == qualifiedItemId)
            .Sum(item => item!.Stack);

    private static int ShippingQuantity() =>
        Game1.getFarm().getShippingBin(Game1.player).Sum(item => item.Stack);

    private HoeDirt? DirtForCurrent() => currentAction?.TargetTile is { } tile
        ? game.GetDirt(new Point(tile.X, tile.Y))
        : null;

    private static bool IsHarvestable(Crop crop) =>
        crop.currentPhase.Value >= crop.phaseDays.Count - 1
        && (!crop.fullyGrown.Value || crop.dayOfCurrentPhase.Value <= 0);

    private void Succeed(string code, string message, bool changed) => CompleteAction("succeeded", code, message, false, changed);

    private void Fail(string code, string message, bool retryable, params RecoverySuggestion[] recovery) =>
        CompleteAction("failed", code, message, retryable, false, recovery);

    private void CompleteAction(
        string status,
        string code,
        string message,
        bool retryable,
        bool stateChanged,
        params RecoverySuggestion[] recovery)
    {
        if (active is null || currentAction is null)
            return;
        currentAction.Trace.Add(new(status == "succeeded" ? "COMPLETED" : "FAILED", game.Tick, code));
        var result = new ActionResult(
            currentAction.PlanAction.ActionId,
            currentAction.PlanAction.Action,
            currentAction.TargetId,
            status,
            code,
            message,
            currentAction.StartedAtUtc,
            DateTimeOffset.UtcNow,
            currentAction.GameTimeBefore,
            game.TimeOfDay,
            1,
            currentAction.Preconditions,
            retryable,
            stateChanged,
            BuildStateDiff(currentAction, stateChanged),
            recovery,
            currentAction.Trace);
        active.Results.Add(result);
        protocol.Publish("action_completed", active.ExecutionId, result);
        if (status == "failed")
        {
            active.Budget.RecordFailure();
            var continueOn = currentAction.PlanAction.ContinueOn ?? Array.Empty<string>();
            if (!continueOn.Contains(code) || active.Budget.Failures > active.Plan.StopConditions.MaxFailures)
            {
                currentAction = null;
                FinishExecution("COMPLETED_WITH_FAILURES", message, true);
                return;
            }
        }
        active.ActionIndex++;
        currentAction = null;
    }

    private StateDiff? BuildStateDiff(ActiveAction action, bool changed)
    {
        if (!changed)
            return null;
        var changes = new List<StateChange>();
        if (Math.Abs(game.Player.Stamina - action.EnergyBefore) > 0.001f)
            changes.Add(new("/player/energy", action.EnergyBefore, game.Player.Stamina));
        if (action.ToolSlot is not null && game.FindWateringCan(out var slot) is { } can && slot == action.ToolSlot)
        {
            if (action.WaterBefore != can.WaterLeft)
                changes.Add(new($"/inventory/{slot}/water", action.WaterBefore, can.WaterLeft));
        }
        if (action.PlanAction.Action == "water_crop" && action.TargetId is not null)
            changes.Add(new($"/entities_by_id/{EscapePointer(action.TargetId)}/properties/watered", false, true));
        if (action.PlanAction.Action == "plant_crop" && action.TargetId is not null)
        {
            changes.Add(new($"/entities_by_id/{EscapePointer(action.TargetId)}/properties/occupied", false, true));
            changes.Add(new($"/inventory/{action.SeedSlot}/quantity", action.InventoryBefore,
                game.Player.Items[action.SeedSlot]?.Stack ?? 0));
        }
        if (action.PlanAction.Action == "harvest_crop" && action.TargetId is not null)
        {
            changes.Add(new($"/entities_by_id/{EscapePointer(action.TargetId)}/properties/harvestable", true, false));
            changes.Add(new("/inventory/total_quantity", action.InventoryBefore, InventoryQuantity()));
        }
        if (action.PlanAction.Action == "clear_debris" && action.TargetId is not null)
            changes.Add(new($"/entities_by_id/{EscapePointer(action.TargetId)}", action.TargetId, null));
        if (action.PlanAction.Action == "buy_item")
        {
            changes.Add(new("/player/money", action.MoneyBefore, game.Player.Money));
            changes.Add(new($"/inventory/by_item/{EscapePointer(action.OfferItemId!)}/quantity",
                action.InventoryBefore, InventoryQuantity(action.OfferItemId!)));
        }
        if (action.PlanAction.Action == "ship_items")
        {
            changes.Add(new("/inventory/selected_quantity", action.InventoryBefore,
                action.InventorySlots.Sum(slot => game.Player.Items[slot]?.Stack ?? 0)));
            changes.Add(new("/economy/shipping_bin_quantity", action.ShippingBefore, ShippingQuantity()));
        }
        if (action.PlanAction.Action == "travel_to")
            changes.Add(new("/game/location", action.LocationBefore, game.LocationName));
        if (action.PlanAction.Action == "sleep")
            changes.Add(new("/game/day_index", action.DayBefore, Game1.Date.TotalDays));
        return new StateDiff(
            action.BaseObservationId,
            changes,
            Array.Empty<Entity>(),
            Array.Empty<Entity>(),
            Array.Empty<RemovedEntity>());
    }

    private static string EscapePointer(string value) => value.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    private void FinishExecution(string status, string message, bool requiresReplan)
    {
        if (active is null)
            return;
        movement.Cancel(status);
        active.Status = status;
        active.Message = message;
        active.RequiresReplan = requiresReplan;
        active.EndedAtUtc = DateTimeOffset.UtcNow;
        try
        {
            active.FinalObservation = observations.Build(previousExecution: active.Summary());
        }
        catch (Exception error)
        {
            active.FinalObservation = null;
            protocol.Publish(
                "observation_invalidated",
                active.ExecutionId,
                new
                {
                    code = "FINAL_OBSERVATION_FAILED",
                    error = error.GetType().Name
                });
        }
        protocol.Publish("execution_state_changed", active.ExecutionId, new { status, message });
        active = null;
        currentAction = null;
        cancelRequested = false;
    }

    private static int Direction(string? facing) => facing switch
    {
        "up" => Game1.up, "right" => Game1.right, "down" => Game1.down, "left" => Game1.left, _ => Game1.down
    };

    private SButton? WarpButton(TilePoint tile)
    {
        if (game.LocationName == "Farm"
            && currentAction?.ExpectedToLocation == "FarmHouse")
        {
            return Game1.options.moveUpButton.Count() > 0
                ? Game1.options.moveUpButton[0].ToSButton()
                : SButton.W;
        }
        var width = game.Location.Map.Layers[0].LayerWidth;
        var height = game.Location.Map.Layers[0].LayerHeight;
        if (tile.X == 0)
            return Game1.options.moveLeftButton.Count() > 0
                ? Game1.options.moveLeftButton[0].ToSButton()
                : SButton.A;
        if (tile.X == width - 1)
            return Game1.options.moveRightButton.Count() > 0
                ? Game1.options.moveRightButton[0].ToSButton()
                : SButton.D;
        if (tile.Y == 0)
            return Game1.options.moveUpButton.Count() > 0
                ? Game1.options.moveUpButton[0].ToSButton()
                : SButton.W;
        if (tile.Y == height - 1)
            return Game1.options.moveDownButton.Count() > 0
                ? Game1.options.moveDownButton[0].ToSButton()
                : SButton.S;
        return null;
    }

    private sealed class MutableExecution
    {
        public MutableExecution(string executionId, ActionPlan plan, DateTimeOffset acceptedAtUtc)
        {
            ExecutionId = executionId;
            Plan = plan;
            AcceptedAtUtc = acceptedAtUtc;
        }

        public string ExecutionId { get; }
        public ActionPlan Plan { get; }
        public DateTimeOffset AcceptedAtUtc { get; }
        public DateTimeOffset? StartedAtUtc { get; set; }
        public DateTimeOffset? EndedAtUtc { get; set; }
        public string Status { get; set; } = "ACCEPTED";
        public string? Message { get; set; }
        public int ActionIndex { get; set; }
        public List<ActionResult> Results { get; } = new();
        public ExecutionBudget Budget { get; } = new();
        public ProtocolObservation? FinalObservation { get; set; }
        public bool RequiresReplan { get; set; }

        public ExecutionResult Snapshot() => new(
            ExecutionId, Plan.PlanId, Status, AcceptedAtUtc, StartedAtUtc, EndedAtUtc,
            Results.ToArray(), Budget.Snapshot(DateTimeOffset.UtcNow), FinalObservation, RequiresReplan, Message);

        public ExecutionSummary Summary() => new(
            ExecutionId,
            Status,
            Message ?? Status,
            Results.Where(result => result.Status == "failed")
                .Select(result => new ActionFailureSummary(result.Action, result.TargetId, result.Code)).ToArray());
    }

    private sealed class ActiveAction
    {
        public ActiveAction(PlanAction planAction, DateTimeOffset startedAtUtc, int gameTimeBefore)
        {
            PlanAction = planAction;
            StartedAtUtc = startedAtUtc;
            GameTimeBefore = gameTimeBefore;
            EnergyBefore = Game1.player.Stamina;
            LocationBefore = Game1.currentLocation.NameOrUniqueName;
            DayBefore = Game1.Date.TotalDays;
            BaseObservationId = string.Empty;
        }

        public PlanAction PlanAction { get; }
        public DateTimeOffset StartedAtUtc { get; }
        public int GameTimeBefore { get; }
        public float EnergyBefore { get; }
        public string LocationBefore { get; }
        public int DayBefore { get; }
        public string BaseObservationId { get; set; }
        public ActionPhase Phase { get; set; }
        public string? TargetId { get; set; }
        public TilePoint? TargetTile { get; set; }
        public int? ToolSlot { get; set; }
        public string? Facing { get; set; }
        public int RemainingTicks { get; set; }
        public int TargetTime { get; set; }
        public int ToolWaitTicks { get; set; }
        public int WaterBefore { get; set; }
        public int SeedSlot { get; set; }
        public int InventoryBefore { get; set; }
        public string? Operation { get; set; }
        public bool WaterAfterPlanting { get; set; }
        public bool UseActionButton { get; set; }
        public string? ExpectedFromLocation { get; set; }
        public string? ExpectedToLocation { get; set; }
        public SButton? WarpButton { get; set; }
        public bool OwnsMenu { get; set; }
        public int StartDay { get; set; }
        public int MenuIndex { get; set; }
        public int Quantity { get; set; }
        public int MoneyBefore { get; set; }
        public string? OfferItemId { get; set; }
        public int ShippingBefore { get; set; }
        public IReadOnlyList<int> InventorySlots { get; set; } = Array.Empty<int>();
        public IReadOnlyList<InteractionTile> InteractionOptions { get; set; } =
            Array.Empty<InteractionTile>();
        public int InteractionIndex { get; set; }
        public bool InputTriggered { get; set; }
        public bool SawToolUse { get; set; }
        public List<PreconditionResult> Preconditions { get; } = new();
        public List<TraceEntry> Trace { get; } = new();
    }

    private enum ActionPhase
    {
        None, Move, Wait, TriggerInput, WaitForInput, WaitForWarp, WaitForDay,
        PerformMenuAction
    }
}

internal sealed class PlanRejectedException : Exception
{
    public PlanRejectedException(ValidationResult validation) : base("The action plan failed validation.") => Validation = validation;
    public ValidationResult Validation { get; }
}

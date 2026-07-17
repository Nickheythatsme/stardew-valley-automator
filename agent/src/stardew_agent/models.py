from __future__ import annotations

from datetime import datetime
from typing import Any, Literal

from pydantic import BaseModel, ConfigDict, Field

LocationName = Literal["Farm", "FarmHouse", "BusStop", "Town", "SeedShop"]
ActionName = Literal[
    "travel_to",
    "clear_debris",
    "plant_crop",
    "water_crop",
    "refill_watering_can",
    "harvest_crop",
    "buy_item",
    "ship_items",
    "wait_until",
    "sleep",
    "advance_dialogue",
    "choose_response",
    "dismiss_menu",
    "finish",
]


class StrictModel(BaseModel):
    model_config = ConfigDict(extra="forbid")


class TilePoint(StrictModel):
    x: int
    y: int


class PixelPoint(StrictModel):
    x: float
    y: float


class InventoryStack(StrictModel):
    slot: int
    qualified_item_id: str
    name: str
    type: str
    quantity: int
    tool_upgrade_level: int | None = None
    water: int | None = None
    water_capacity: int | None = None


class GameState(StrictModel):
    save_id: str
    time: int
    season: Literal["spring", "summer", "fall", "winter"]
    day: int
    year: int
    weather: str
    location: LocationName
    world_ready: bool
    paused: bool
    active_menu: str | None = None
    event_active: bool


class PlayerState(StrictModel):
    tile: TilePoint
    pixel: PixelPoint
    facing: Literal["up", "right", "down", "left"]
    energy: float
    max_energy: int
    health: int
    max_health: int
    money: int
    busy: bool
    using_tool: bool
    selected_slot: int


class InteractionTile(StrictModel):
    tile: TilePoint
    facing: Literal["up", "right", "down", "left"]
    reachable: bool
    path_cost: int | None = None


class Entity(StrictModel):
    id: str
    revision: str
    kind: Literal[
        "crop",
        "planting_tile",
        "debris",
        "container",
        "water_source",
        "shipping_bin",
        "shop",
        "bed",
    ]
    location: LocationName
    tile: TilePoint
    reachable: bool
    path_cost: int | None = None
    interaction_tiles: list[InteractionTile]
    properties: dict[str, Any]


class LocalGrid(StrictModel):
    origin: TilePoint
    width: int = Field(ge=1, le=31)
    height: int = Field(ge=1, le=31)
    rows: list[str]
    legend: dict[str, str]


class ObservationSummary(StrictModel):
    dry_crops: int
    harvestable_crops: int
    empty_tilled_tiles: int
    reachable_water_sources: int
    containers: int
    omitted_entities: dict[str, int]


class RouteEdge(StrictModel):
    from_location: LocationName
    to_location: LocationName
    departure_tile: TilePoint
    arrival_tile: TilePoint


class UiChoice(StrictModel):
    id: str
    label: str


class UiState(StrictModel):
    kind: Literal[
        "dialogue",
        "question",
        "shop",
        "shipping",
        "sleep_confirmation",
        "overnight_summary",
        "level_up",
        "unknown",
    ]
    menu_type: str
    text: str | None = None
    choices: list[UiChoice] = Field(default_factory=list, max_length=32)
    owned_by_executor: bool = False


class ShopOffer(StrictModel):
    id: str
    revision: str
    shop_id: str
    qualified_item_id: str
    name: str
    price: int = Field(ge=0)
    stock: int | None = None
    available: bool
    is_seed: bool
    crop_id: str | None = None
    growth_days: int | None = None
    seasons: list[Literal["spring", "summer", "fall", "winter"]] = Field(default_factory=list)
    harvest_method: Literal["hand", "scythe", "unknown"] | None = None
    trellis: bool = False
    regrow_days: int | None = None


class EconomyState(StrictModel):
    wallet: int
    pending_shipping_value: int = 0
    seed_shop_open: bool = False
    seed_shop_next_open_time: int | None = None


class Observation(StrictModel):
    schema_version: Literal["2.0"]
    observation_id: str
    world_revision: int
    captured_at_utc: datetime
    game: GameState
    player: PlayerState
    inventory: list[InventoryStack]
    local_grid: LocalGrid
    entities: list[Entity]
    summary: ObservationSummary
    routes: list[RouteEdge] = Field(default_factory=list)
    ui_state: UiState | None = None
    shop_offers: list[ShopOffer] = Field(default_factory=list, max_length=128)
    economy: EconomyState
    previous_execution: dict[str, Any] | None = None


class StopConditions(StrictModel):
    energy_below: float = Field(ge=0, le=1000)
    game_time_at_or_after: int = Field(ge=600, le=2600)
    max_failures: int = Field(ge=0, le=64)


class PlanAction(StrictModel):
    action_id: str = Field(min_length=1, max_length=128)
    action: ActionName
    args: dict[str, Any]
    continue_on: list[Literal["ALREADY_SATISFIED", "TARGET_UNREACHABLE"]] | None = None


class ActionPlan(StrictModel):
    schema_version: Literal["2.0"]
    plan_id: str = Field(min_length=1, max_length=128)
    expected_observation_id: str
    goal: str = Field(min_length=1, max_length=512)
    actions: list[PlanAction] = Field(min_length=1, max_length=64)
    stop_conditions: StopConditions
    request_replan_after: bool


class PlannerActionArgs(StrictModel):
    """OpenAI Structured Outputs DTO: every field is required and nullable."""

    target_id: str | None
    target_revision: str | None
    destination: LocationName | None
    qualified_item_id: str | None
    offer_id: str | None
    offer_revision: str | None
    quantity: int | None
    source_id: str | None
    water_after_planting: bool | None
    selector_item_ids: list[str] | None
    category: Literal["crop", "forage", "seed"] | None
    time: int | None
    response_id: str | None
    reason: str | None


class PlannerAction(StrictModel):
    action_id: str = Field(min_length=1, max_length=128)
    action: ActionName
    args: PlannerActionArgs
    continue_on: list[Literal["ALREADY_SATISFIED", "TARGET_UNREACHABLE"]]


class PlannerActionPlan(StrictModel):
    schema_version: Literal["2.0"]
    plan_id: str = Field(min_length=1, max_length=128)
    expected_observation_id: str
    goal: str = Field(min_length=1, max_length=512)
    actions: list[PlannerAction] = Field(min_length=1, max_length=32)
    stop_conditions: StopConditions
    request_replan_after: bool

    def to_protocol_plan(self) -> ActionPlan:
        actions = []
        for planner_action in self.actions:
            args = {
                key: value
                for key, value in planner_action.args.model_dump(mode="json").items()
                if value is not None
            }
            actions.append(
                PlanAction(
                    action_id=planner_action.action_id,
                    action=planner_action.action,
                    args=args,
                    continue_on=planner_action.continue_on or None,
                )
            )
        return ActionPlan(
            schema_version=self.schema_version,
            plan_id=self.plan_id,
            expected_observation_id=self.expected_observation_id,
            goal=self.goal,
            actions=actions,
            stop_conditions=self.stop_conditions,
            request_replan_after=self.request_replan_after,
        )


class GoalSpec(StrictModel):
    type: Literal["wallet_at_least"]
    target_money: int = Field(ge=1, le=100_000_000)
    income_domain: Literal["crops"]
    original_goal: str


class GoalCheckpoint(StrictModel):
    goal: GoalSpec
    status: Literal["RUNNING", "COMPLETED", "BUDGET_EXCEEDED", "STOPPED"]
    initial_day_index: int
    current_day_index: int
    model_calls: int
    executions: int
    last_observation_id: str | None = None
    message: str | None = None


class ProtocolError(StrictModel):
    code: str
    message: str
    details: Any = None


class ResponseEnvelope(StrictModel):
    protocol_version: Literal["1.0"]
    type: Literal["response"]
    request_id: str
    ok: bool
    result: Any = None
    error: ProtocolError | None = None


class EventEnvelope(StrictModel):
    protocol_version: Literal["1.0"]
    type: Literal["event"]
    sequence: int
    event: str
    execution_id: str | None = None
    payload: Any


class ExecutionResult(StrictModel):
    execution_id: str
    plan_id: str
    status: str
    accepted_at_utc: datetime
    started_at_utc: datetime | None = None
    ended_at_utc: datetime | None = None
    actions: list[dict[str, Any]]
    budget_usage: dict[str, Any]
    final_observation: Observation | None = None
    requires_replan: bool
    message: str | None = None

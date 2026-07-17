from __future__ import annotations

from datetime import datetime
from typing import Any, Literal

from pydantic import BaseModel, ConfigDict, Field


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
    location: str
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
    kind: Literal["crop", "tilled_soil", "container", "water_source"]
    location: str
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


class Observation(StrictModel):
    schema_version: Literal["1.0"]
    observation_id: str
    world_revision: int
    captured_at_utc: datetime
    game: GameState
    player: PlayerState
    inventory: list[InventoryStack]
    local_grid: LocalGrid
    entities: list[Entity]
    summary: ObservationSummary
    previous_execution: dict[str, Any] | None = None


class StopConditions(StrictModel):
    energy_below: float = Field(ge=0, le=1000)
    game_time_at_or_after: int = Field(ge=600, le=2600)
    max_failures: int = Field(ge=0, le=64)


class PlanAction(StrictModel):
    action_id: str = Field(min_length=1, max_length=128)
    action: Literal[
        "move_to",
        "water_crop",
        "refill_watering_can",
        "harvest_crop",
        "plant_seed",
        "deposit_items",
        "wait",
        "finish",
    ]
    args: dict[str, Any]
    continue_on: list[Literal["ALREADY_SATISFIED", "TARGET_UNREACHABLE"]] | None = None


class ActionPlan(StrictModel):
    schema_version: Literal["1.0"]
    plan_id: str = Field(min_length=1, max_length=128)
    expected_observation_id: str
    goal: str = Field(min_length=1, max_length=512)
    actions: list[PlanAction] = Field(min_length=1, max_length=64)
    stop_conditions: StopConditions
    request_replan_after: bool


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

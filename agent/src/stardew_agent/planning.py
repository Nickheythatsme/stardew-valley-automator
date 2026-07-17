from __future__ import annotations

import uuid

from .models import ActionPlan, Observation, PlanAction, StopConditions


def plan_water_one(observation: Observation) -> ActionPlan:
    crops = [
        entity
        for entity in observation.entities
        if entity.kind == "crop"
        and entity.reachable
        and entity.properties.get("needs_watering") is True
        and entity.properties.get("watered") is False
    ]
    if not crops:
        return ActionPlan(
            schema_version="2.0",
            plan_id=f"plan-{uuid.uuid4()}",
            expected_observation_id=observation.observation_id,
            goal="Water one reachable dry crop",
            actions=[
                PlanAction(
                    action_id="finish-1",
                    action="finish",
                    args={"reason": "No reachable dry crop needs watering."},
                )
            ],
            stop_conditions=StopConditions(
                energy_below=25, game_time_at_or_after=1200, max_failures=1
            ),
            request_replan_after=False,
        )

    target = min(crops, key=lambda entity: (entity.path_cost or 10**9, entity.id))
    actions: list[PlanAction] = []
    watering_can = next(
        (item for item in observation.inventory if item.type == "tool" and item.water is not None),
        None,
    )
    if watering_can is not None and watering_can.water == 0:
        sources = [
            entity
            for entity in observation.entities
            if entity.kind == "water_source" and entity.reachable
        ]
        if sources:
            source = min(sources, key=lambda entity: (entity.path_cost or 10**9, entity.id))
            actions.append(
                PlanAction(
                    action_id="refill-1",
                    action="refill_watering_can",
                    args={"source_id": source.id},
                )
            )
    actions.extend(
        [
            PlanAction(
                action_id="water-1",
                action="water_crop",
                args={"target_id": target.id, "target_revision": target.revision},
            ),
            PlanAction(
                action_id="finish-1",
                action="finish",
                args={"reason": "The selected reachable crop was watered."},
            ),
        ]
    )
    return ActionPlan(
        schema_version="2.0",
        plan_id=f"plan-{uuid.uuid4()}",
        expected_observation_id=observation.observation_id,
        goal="Water one reachable dry crop",
        actions=actions,
        stop_conditions=StopConditions(energy_below=25, game_time_at_or_after=1200, max_failures=1),
        request_replan_after=False,
    )

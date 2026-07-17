from __future__ import annotations

import json
from importlib.resources import files
from typing import Any

from stardew_agent.models import Observation


class PromptBuilder:
    def system_prompt(self) -> str:
        return files("stardew_agent.prompts").joinpath("system.txt").read_text(encoding="utf-8")

    def user_prompt(
        self,
        *,
        goal: str,
        observation: Observation,
        actions: dict[str, Any],
        remaining_budget: dict[str, Any],
        recent_executions: list[dict[str, Any]],
        memory: list[dict[str, Any]],
    ) -> str:
        sections = [
            ("GOAL", goal),
            ("AVAILABLE ACTIONS", json.dumps(actions, separators=(",", ":"), sort_keys=True)),
            (
                "REMAINING BUDGET",
                json.dumps(remaining_budget, separators=(",", ":"), sort_keys=True),
            ),
            (
                "CURRENT OBSERVATION",
                json.dumps(
                    self._compact_observation(observation),
                    separators=(",", ":"),
                    sort_keys=True,
                ),
            ),
            (
                "RECENT VERIFIED EXECUTIONS",
                json.dumps(recent_executions, separators=(",", ":"), sort_keys=True),
            ),
            ("RELEVANT VERIFIED MEMORY", json.dumps(memory, separators=(",", ":"), sort_keys=True)),
        ]
        return (
            "\n\n".join(f"{heading}\n{body}" for heading, body in sections)
            + "\n\nReturn one ACTION_PLAN_SCHEMA JSON object."
        )

    @staticmethod
    def repair_prompt(original_prompt: str, validation_errors: str) -> str:
        return (
            f"{original_prompt}\n\n"
            "YOUR PREVIOUS PLAN WAS REJECTED\n"
            f"{validation_errors}\n\n"
            "Return a corrected plan for the same observation. Do not repeat invalid fields."
        )

    @staticmethod
    def _compact_observation(observation: Observation) -> dict[str, Any]:
        """Project protocol truth into a bounded, goal-relevant model context."""

        kind_limits = {
            "crop": 64,
            "planting_tile": 16,
            "debris": 16,
            "water_source": 8,
            "shipping_bin": 4,
            "shop": 4,
            "bed": 4,
        }
        selected: list[dict[str, Any]] = []
        counts: dict[str, int] = {}
        entities = sorted(
            observation.entities,
            key=lambda entity: (
                entity.location != observation.game.location,
                not entity.reachable,
                entity.path_cost if entity.path_cost is not None else 10**9,
                entity.id,
            ),
        )
        for entity in entities:
            limit = kind_limits.get(entity.kind, 0)
            used = counts.get(entity.kind, 0)
            if used >= limit:
                continue
            counts[entity.kind] = used + 1
            selected.append(
                {
                    "id": entity.id,
                    "revision": entity.revision,
                    "kind": entity.kind,
                    "location": entity.location,
                    "tile": entity.tile.model_dump(mode="json"),
                    "reachable": entity.reachable,
                    "path_cost": entity.path_cost,
                    "interaction_tiles": [
                        interaction.model_dump(mode="json")
                        for interaction in entity.interaction_tiles
                        if interaction.reachable
                    ][:2],
                    "properties": entity.properties,
                }
            )

        offers = [
            offer.model_dump(mode="json")
            for offer in observation.shop_offers
            if offer.available
            and offer.is_seed
            and not offer.trellis
            and offer.regrow_days is None
            and offer.harvest_method == "hand"
            and observation.game.season in offer.seasons
        ][:16]
        return {
            "schema_version": observation.schema_version,
            "observation_id": observation.observation_id,
            "world_revision": observation.world_revision,
            "game": observation.game.model_dump(mode="json"),
            "player": observation.player.model_dump(mode="json"),
            "inventory": [
                item.model_dump(mode="json", exclude_none=True)
                for item in observation.inventory
            ],
            "local_grid": observation.local_grid.model_dump(mode="json"),
            "entities": selected,
            "summary": observation.summary.model_dump(mode="json"),
            "routes": [
                route.model_dump(mode="json")
                for route in observation.routes
                if route.from_location == observation.game.location
            ],
            "ui_state": (
                observation.ui_state.model_dump(mode="json")
                if observation.ui_state is not None
                else None
            ),
            "shop_offers": offers,
            "economy": observation.economy.model_dump(mode="json"),
            "previous_execution": observation.previous_execution,
            "projection": {
                "entity_limits": kind_limits,
                "included_by_kind": counts,
                "protocol_entity_count": len(observation.entities),
            },
        }

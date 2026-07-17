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
                    observation.model_dump(mode="json"), separators=(",", ":"), sort_keys=True
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

from __future__ import annotations

import json
from pathlib import Path
from typing import Any

from .models import ActionPlan, Observation
from .prompts import PromptBuilder
from .providers import PlanProvider
from .validation import PlanValidator, repository_root


class AgentPlanner:
    def __init__(self, provider: PlanProvider, schema_path: Path | None = None) -> None:
        self.provider = provider
        self.schema_path = schema_path or repository_root() / "schemas" / "action-plan.schema.json"
        self.schema = json.loads(self.schema_path.read_text(encoding="utf-8"))
        self.validator = PlanValidator(self.schema_path)
        self.prompts = PromptBuilder()

    async def plan(
        self,
        *,
        goal: str,
        observation: Observation,
        actions: dict[str, Any],
        remaining_budget: dict[str, Any],
        recent_executions: list[dict[str, Any]],
        memory: list[dict[str, Any]],
    ) -> ActionPlan:
        user_prompt = self.prompts.user_prompt(
            goal=goal,
            observation=observation,
            actions=actions,
            remaining_budget=remaining_budget,
            recent_executions=recent_executions[-3:],
            memory=memory,
        )
        raw = await self.provider.generate_plan(
            system_prompt=self.prompts.system_prompt(),
            user_prompt=user_prompt,
            schema=self.schema,
            temperature=0.1,
        )
        result = self.validator.validate(raw, observation)
        if not result.valid or result.plan is None:
            details = "; ".join(f"{issue.path}: {issue.message}" for issue in result.issues)
            raise ValueError(f"Provider returned an invalid plan: {details}")
        return result.plan

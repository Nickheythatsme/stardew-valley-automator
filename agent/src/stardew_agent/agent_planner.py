from __future__ import annotations

import json
from collections.abc import Callable
from dataclasses import dataclass
from datetime import UTC, datetime
from pathlib import Path
from typing import Any

from .models import ActionPlan, Observation
from .prompts import PromptBuilder
from .providers import PlanProvider, ProviderFailure, ProviderResult
from .validation import PlanValidator, repository_root


@dataclass(frozen=True)
class PlanningResult:
    plan: ActionPlan
    provider_results: tuple[ProviderResult, ...]

    @property
    def model_calls(self) -> int:
        return sum(result.attempts for result in self.provider_results)


class PlanningFailure(ValueError):
    def __init__(self, message: str, model_calls: int) -> None:
        super().__init__(message)
        self.model_calls = model_calls


class AgentPlanner:
    def __init__(
        self,
        provider: PlanProvider,
        schema_path: Path | None = None,
        debug_sink: Callable[[dict[str, Any]], None] | None = None,
    ) -> None:
        self.provider = provider
        self.schema_path = schema_path or repository_root() / "schemas" / "action-plan.schema.json"
        self.schema = json.loads(self.schema_path.read_text(encoding="utf-8"))
        self.validator = PlanValidator(self.schema_path)
        self.prompts = PromptBuilder()
        self.debug_sink = debug_sink

    async def plan(
        self,
        *,
        goal: str,
        observation: Observation,
        actions: dict[str, Any],
        remaining_budget: dict[str, Any],
        recent_executions: list[dict[str, Any]],
        memory: list[dict[str, Any]],
        max_model_requests: int,
    ) -> PlanningResult:
        user_prompt = self.prompts.user_prompt(
            goal=goal,
            observation=observation,
            actions=actions,
            remaining_budget=remaining_budget,
            recent_executions=recent_executions[-3:],
            memory=memory,
        )
        system_prompt = self.prompts.system_prompt()
        self._debug(
            "request",
            stage="plan",
            system_prompt=system_prompt,
            user_prompt=user_prompt,
            max_attempts=max_model_requests,
        )
        try:
            provider_result = await self.provider.generate_plan(
                system_prompt=system_prompt,
                user_prompt=user_prompt,
                max_attempts=max_model_requests,
            )
        except Exception as error:
            self._debug("provider_error", stage="plan", error=repr(error))
            raise
        self._debug_provider_result("plan", provider_result)
        plan = provider_result.plan.to_protocol_plan()
        self._bind_snapshot_revisions(plan, observation)
        result = self.validator.validate(
            plan.model_dump(mode="json", exclude_none=True), observation
        )
        self._debug_validation("plan", plan, result.issues)
        if not result.valid or result.plan is None:
            details = "; ".join(f"{issue.path}: {issue.message}" for issue in result.issues)
            remaining_requests = max_model_requests - provider_result.attempts
            if remaining_requests <= 0:
                raise PlanningFailure(
                    f"Provider returned an invalid plan and no repair request remains: {details}",
                    provider_result.attempts,
                )
            repair_prompt = self.prompts.repair_prompt(user_prompt, details)
            self._debug(
                "request",
                stage="repair",
                system_prompt=system_prompt,
                user_prompt=repair_prompt,
                max_attempts=remaining_requests,
            )
            try:
                repair_result = await self.provider.generate_plan(
                    system_prompt=system_prompt,
                    user_prompt=repair_prompt,
                    max_attempts=remaining_requests,
                )
            except ProviderFailure as error:
                self._debug("provider_error", stage="repair", error=repr(error))
                raise PlanningFailure(
                    str(error), provider_result.attempts + error.attempts
                ) from error
            self._debug_provider_result("repair", repair_result)
            repaired_plan = repair_result.plan.to_protocol_plan()
            self._bind_snapshot_revisions(repaired_plan, observation)
            repaired = self.validator.validate(
                repaired_plan.model_dump(mode="json", exclude_none=True), observation
            )
            self._debug_validation("repair", repaired_plan, repaired.issues)
            if not repaired.valid or repaired.plan is None:
                repaired_details = "; ".join(
                    f"{issue.path}: {issue.message}" for issue in repaired.issues
                )
                raise PlanningFailure(
                    f"Provider returned an invalid repaired plan: {repaired_details}",
                    provider_result.attempts + repair_result.attempts,
                )
            return PlanningResult(repaired.plan, (provider_result, repair_result))
        return PlanningResult(result.plan, (provider_result,))

    def _debug(self, event: str, **payload: Any) -> None:
        if self.debug_sink is None:
            return
        self.debug_sink(
            {
                "captured_at_utc": datetime.now(UTC).isoformat(),
                "event": event,
                **payload,
            }
        )

    def _debug_provider_result(self, stage: str, result: ProviderResult) -> None:
        self._debug(
            "response",
            stage=stage,
            response_id=result.response_id,
            model=result.model,
            input_tokens=result.input_tokens,
            output_tokens=result.output_tokens,
            attempts=result.attempts,
            parsed_plan=result.plan.model_dump(mode="json"),
        )

    def _debug_validation(
        self,
        stage: str,
        plan: ActionPlan,
        issues: tuple[Any, ...],
    ) -> None:
        self._debug(
            "validation",
            stage=stage,
            valid=not issues,
            normalized_plan=plan.model_dump(mode="json", exclude_none=True),
            issues=[
                {"path": issue.path, "code": issue.code, "message": issue.message}
                for issue in issues
            ],
        )

    @staticmethod
    def _bind_snapshot_revisions(plan: ActionPlan, observation: Observation) -> None:
        """Bind selected IDs to exact revisions from the plan's expected snapshot."""

        if plan.expected_observation_id != observation.observation_id:
            return
        entities = {entity.id: entity for entity in observation.entities}
        offers = {offer.id: offer for offer in observation.shop_offers}
        for action in plan.actions:
            target_id = action.args.get("target_id")
            if isinstance(target_id, str) and target_id in entities:
                action.args["target_revision"] = entities[target_id].revision
            offer_id = action.args.get("offer_id")
            if isinstance(offer_id, str) and offer_id in offers:
                action.args["offer_revision"] = offers[offer_id].revision

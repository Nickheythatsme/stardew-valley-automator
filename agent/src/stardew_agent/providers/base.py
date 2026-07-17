from __future__ import annotations

from dataclasses import dataclass
from typing import Protocol

from ..models import PlannerActionPlan


@dataclass(frozen=True)
class ProviderResult:
    plan: PlannerActionPlan
    response_id: str
    model: str
    input_tokens: int
    output_tokens: int
    attempts: int
    prompt_hash: str


class PlanProvider(Protocol):
    """Provider-neutral boundary for one schema-constrained plan request."""

    async def generate_plan(
        self,
        *,
        system_prompt: str,
        user_prompt: str,
        max_attempts: int,
    ) -> ProviderResult: ...

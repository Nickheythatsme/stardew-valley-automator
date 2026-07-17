from __future__ import annotations

from typing import Any, Protocol


class PlanProvider(Protocol):
    """Provider-neutral boundary for one schema-constrained plan request."""

    async def generate_plan(
        self,
        *,
        system_prompt: str,
        user_prompt: str,
        schema: dict[str, Any],
        temperature: float = 0.1,
    ) -> dict[str, Any]: ...

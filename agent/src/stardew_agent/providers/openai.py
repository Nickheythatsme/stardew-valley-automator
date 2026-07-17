from __future__ import annotations

import asyncio
import hashlib
from typing import Any

from openai import (
    APIConnectionError,
    APITimeoutError,
    AsyncOpenAI,
    InternalServerError,
    RateLimitError,
)

from ..models import PlannerActionPlan
from .base import ProviderResult


class ProviderFailure(RuntimeError):
    def __init__(self, code: str, message: str, attempts: int) -> None:
        super().__init__(f"{code}: {message}")
        self.code = code
        self.attempts = attempts


class OpenAIPlanProvider:
    def __init__(
        self,
        *,
        api_key: str,
        model: str = "gpt-5.6-terra",
        reasoning_effort: str = "medium",
        timeout_seconds: float = 60,
        max_attempts: int = 3,
    ) -> None:
        if not api_key:
            raise ValueError("OPENAI_API_KEY is required.")
        self.model = model
        self.reasoning_effort = reasoning_effort
        self.max_attempts = max_attempts
        self.client = AsyncOpenAI(api_key=api_key, timeout=timeout_seconds, max_retries=0)

    async def generate_plan(
        self,
        *,
        system_prompt: str,
        user_prompt: str,
        max_attempts: int,
    ) -> ProviderResult:
        allowed_attempts = max(1, min(self.max_attempts, max_attempts))
        last_error: Exception | None = None
        for attempt in range(1, allowed_attempts + 1):
            try:
                response = await self.client.responses.parse(
                    model=self.model,
                    reasoning={"effort": self.reasoning_effort},
                    input=[
                        {"role": "system", "content": system_prompt},
                        {"role": "user", "content": user_prompt},
                    ],
                    text_format=PlannerActionPlan,
                )
                parsed = response.output_parsed
                if parsed is None:
                    refusal = _refusal_text(response)
                    raise ProviderFailure(
                        "MODEL_REFUSAL" if refusal else "UNPARSEABLE_OUTPUT",
                        refusal or "OpenAI returned no parsed action plan.",
                        attempt,
                    )
                usage = getattr(response, "usage", None)
                return ProviderResult(
                    plan=parsed,
                    response_id=str(response.id),
                    model=self.model,
                    input_tokens=int(getattr(usage, "input_tokens", 0) or 0),
                    output_tokens=int(getattr(usage, "output_tokens", 0) or 0),
                    attempts=attempt,
                    prompt_hash="sha256:"
                    + hashlib.sha256(
                        (system_prompt + "\n" + user_prompt).encode()
                    ).hexdigest(),
                )
            except ProviderFailure:
                raise
            except (
                APIConnectionError,
                APITimeoutError,
                RateLimitError,
                InternalServerError,
            ) as error:
                last_error = error
                if attempt == allowed_attempts:
                    break
                await asyncio.sleep(0.5 * (2 ** (attempt - 1)))
        assert last_error is not None
        raise ProviderFailure("PROVIDER_UNAVAILABLE", str(last_error), allowed_attempts)


def _refusal_text(response: Any) -> str | None:
    for output in getattr(response, "output", []):
        for content in getattr(output, "content", []):
            refusal = getattr(content, "refusal", None)
            if refusal:
                return str(refusal)
    return None

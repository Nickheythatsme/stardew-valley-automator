from __future__ import annotations

from pathlib import Path

from .client import GameClient
from .planning import plan_water_one
from .session import SessionStore
from .validation import PlanValidator


async def water_one(endpoint: Path, runs_root: Path) -> int:
    client = GameClient.from_endpoint_file(endpoint)
    store = SessionStore(runs_root, "Water one reachable dry crop")
    try:
        await client.connect()
        observation = await client.observe()
        store.observation(observation)
        plan = plan_water_one(observation)
        validation = PlanValidator().validate(
            plan.model_dump(mode="json", exclude_none=True), observation
        )
        if not validation.valid or validation.plan is None:
            store.append(
                "turns",
                {
                    "status": "validation_failed",
                    "issues": [issue.__dict__ for issue in validation.issues],
                },
            )
            return 2
        store.plan(1, validation.plan)
        execution_id = await client.execute_plan(validation.plan)
        result = await client.wait_for_execution(execution_id)
        store.append("executions", result)
        if result.final_observation is not None:
            store.observation(result.final_observation)
        return 0 if result.status == "COMPLETED" else 1
    finally:
        await client.close()
        store.close()

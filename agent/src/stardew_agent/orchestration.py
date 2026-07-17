from __future__ import annotations

import asyncio
from pathlib import Path
from time import monotonic
from typing import Any

from .agent_planner import AgentPlanner, PlanningFailure
from .client import GameClient, GameProtocolError
from .goals import compile_goal, game_day_index, goal_completed
from .memory import EpisodicMemory
from .models import GoalCheckpoint, GoalSpec
from .planning import plan_water_one
from .providers import PlanProvider, ProviderFailure
from .session import SessionStore
from .validation import PlanValidator

ACTION_CATALOG: dict[str, dict[str, Any]] = {
    "travel_to": {
        "required": ["destination"],
        "direct_observed_route_only": True,
        "guidance": (
            "Use an observed direct route to change locations. To end a Farm workday, "
            "travel to FarmHouse before using sleep."
        ),
    },
    "clear_debris": {"required": ["target_id", "target_revision"]},
    "plant_crop": {
        "required": [
            "target_id",
            "target_revision",
            "qualified_item_id",
            "water_after_planting",
        ]
    },
    "water_crop": {"required": ["target_id", "target_revision"]},
    "refill_watering_can": {"optional": ["source_id"]},
    "harvest_crop": {"required": ["target_id", "target_revision"]},
    "buy_item": {"required": ["offer_id", "offer_revision", "quantity"]},
    "ship_items": {"one_of": ["selector_item_ids", "category"]},
    "wait_until": {
        "required": ["time"],
        "guidance": (
            "Only for a short same-day timing requirement. Never wait until 2600 or "
            "use this as a substitute for going home and sleeping."
        ),
    },
    "sleep": {
        "required": [],
        "guidance": (
            "Use only after the observation confirms current location FarmHouse. "
            "This semantic action walks to the bed, enters it, confirms Yes, and "
            "waits for the next day."
        ),
    },
    "advance_dialogue": {"required": []},
    "choose_response": {"required": ["response_id"]},
    "dismiss_menu": {"required": []},
    "finish": {"required": ["reason"]},
}


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
        print(f"water-one: {result.status} ({result.message or 'no message'})")
        print(f"session: {store.path}")
        return 0 if result.status == "COMPLETED" else 1
    finally:
        await client.close()
        store.close()


async def run_goal(
    *,
    goal_text: str,
    endpoint: Path,
    runs_root: Path,
    provider: PlanProvider,
    max_game_days: int = 14,
    max_model_calls: int = 64,
    max_wall_minutes: int = 120,
    resume_path: Path | None = None,
) -> int:
    goal = compile_goal(goal_text)
    store = (
        SessionStore.resume(runs_root, resume_path)
        if resume_path is not None
        else SessionStore(runs_root, goal.original_goal)
    )
    memory = EpisodicMemory(runs_root / "memory.db")
    client = GameClient.from_endpoint_file(endpoint)
    recent_results: list[dict[str, Any]] = []
    started = monotonic()
    executions = 0
    model_calls = 0
    initial_day: int | None = None
    try:
        hello = await client.connect()
        observation = await client.observe()
        store.observation(observation)
        if observation.game.paused:
            print(
                "Stardew is paused. Focus the game window or disable "
                "'Pause When Game Window Is Inactive'; waiting up to 60 seconds."
            )
            for _ in range(60):
                await asyncio.sleep(1)
                observation = await client.observe()
                if not observation.game.paused:
                    store.observation(observation)
                    break
            else:
                message = "The game remained paused for 60 seconds; no model call was made."
                store.finish("STOPPED", message)
                print(message)
                return 6
        current_day = game_day_index(observation)
        if resume_path is not None:
            saved = GoalCheckpoint.model_validate(store.read_checkpoint())
            goal = saved.goal
            initial_day = saved.initial_day_index
            executions = saved.executions
            model_calls = saved.model_calls
        else:
            initial_day = current_day

        print(
            f"connected: save={observation.game.save_id} "
            f"location={observation.game.location} money={observation.player.money}"
        )
        if goal_completed(goal, observation):
            message = f"Goal already satisfied: wallet is {observation.player.money}."
            store.finish("COMPLETED", message)
            print(message)
            return 0

        planner = AgentPlanner(provider, debug_sink=store.llm_debug)
        print(f"LLM debug log: {store.path / 'llm-debug.jsonl'}")
        turn = executions + 1
        while True:
            elapsed_minutes = (monotonic() - started) / 60
            current_day = game_day_index(observation)
            days_used = current_day - initial_day
            if (
                days_used >= max_game_days
                or model_calls >= max_model_calls
                or elapsed_minutes >= max_wall_minutes
            ):
                message = (
                    f"Budget exceeded: days={days_used}/{max_game_days}, "
                    f"model_calls={model_calls}/{max_model_calls}, "
                    f"minutes={elapsed_minutes:.1f}/{max_wall_minutes}."
                )
                checkpoint = _checkpoint(
                    goal,
                    "BUDGET_EXCEEDED",
                    initial_day,
                    current_day,
                    model_calls,
                    executions,
                    observation.observation_id,
                    message,
                )
                store.checkpoint(checkpoint)
                store.finish("BUDGET_EXCEEDED", message)
                print(message)
                return 3

            capabilities = set(hello.get("capabilities", ACTION_CATALOG))
            actions = {
                name: definition
                for name, definition in ACTION_CATALOG.items()
                if name in capabilities
            }
            remaining = {
                "game_days": max_game_days - days_used,
                "model_calls": max_model_calls - model_calls,
                "wall_minutes": max(0, max_wall_minutes - elapsed_minutes),
                "max_actions_this_plan": 32,
            }
            print(
                f"planning turn {turn}: day={observation.game.day} "
                f"time={observation.game.time} money={observation.player.money}"
            )
            try:
                planned = await planner.plan(
                    goal=goal.original_goal,
                    observation=observation,
                    actions=actions,
                    remaining_budget=remaining,
                    recent_executions=recent_results[-3:],
                    memory=memory.relevant(
                        observation.game.save_id, observation.game.location
                    ),
                    max_model_requests=max_model_calls - model_calls,
                )
            except ProviderFailure as error:
                model_calls += error.attempts
                message = str(error)
                store.finish("STOPPED", message)
                print(message)
                return 5
            except PlanningFailure as error:
                model_calls += error.model_calls
                message = str(error)
                store.finish("STOPPED", message)
                print(message)
                return 5

            model_calls += planned.model_calls
            for provider_result in planned.provider_results:
                store.provider_call(
                    {
                        "turn": turn,
                        "response_id": provider_result.response_id,
                        "model": provider_result.model,
                        "input_tokens": provider_result.input_tokens,
                        "output_tokens": provider_result.output_tokens,
                        "attempts": provider_result.attempts,
                        "prompt_hash": provider_result.prompt_hash,
                    }
                )
                print(
                    "OpenAI usage: "
                    f"input={provider_result.input_tokens} "
                    f"output={provider_result.output_tokens} tokens"
                )
            store.plan(turn, planned.plan)
            print(
                f"executing {len(planned.plan.actions)} actions "
                f"(model calls used: {model_calls}/{max_model_calls})"
            )
            try:
                execution_id = await client.execute_plan(planned.plan)
            except GameProtocolError as error:
                if error.code != "VALIDATION_FAILED":
                    raise
                recent_results.append(
                    {
                        "status": "VALIDATION_FAILED",
                        "message": str(error),
                        "actions": [],
                    }
                )
                store.append(
                    "executions",
                    {
                        "status": "VALIDATION_FAILED",
                        "message": str(error),
                        "plan_id": planned.plan.plan_id,
                    },
                )
                observation = await client.observe()
                store.observation(observation)
                turn += 1
                continue
            result = await client.wait_for_execution(execution_id)
            executions += 1
            store.append("executions", result)
            recent_results.append(_compact_execution(result))
            observation = result.final_observation or await client.observe()
            store.observation(observation)
            memory.update_from_verified_observation(observation)

            if goal_completed(goal, observation):
                message = f"Goal completed with wallet={observation.player.money}."
                checkpoint = _checkpoint(
                    goal,
                    "COMPLETED",
                    initial_day,
                    game_day_index(observation),
                    model_calls,
                    executions,
                    observation.observation_id,
                    message,
                )
                store.checkpoint(checkpoint)
                store.finish("COMPLETED", message)
                print(message)
                print(f"session: {store.path}")
                return 0

            checkpoint = _checkpoint(
                goal,
                "RUNNING",
                initial_day,
                game_day_index(observation),
                model_calls,
                executions,
                observation.observation_id,
                result.message,
            )
            store.checkpoint(checkpoint)
            print(
                f"execution {result.status}: money={observation.player.money}; "
                f"{result.message or 'replanning'}"
            )
            turn += 1
            await asyncio.sleep(0)
    except KeyboardInterrupt:
        store.finish("STOPPED", "Cancelled by user.")
        return 4
    except Exception as error:
        store.finish("STOPPED", str(error))
        raise
    finally:
        await client.close()
        memory.close()
        store.close()


def _checkpoint(
    goal: GoalSpec,
    status: str,
    initial_day: int,
    current_day: int,
    model_calls: int,
    executions: int,
    observation_id: str | None,
    message: str | None,
) -> GoalCheckpoint:
    return GoalCheckpoint(
        goal=goal,
        status=status,  # type: ignore[arg-type]
        initial_day_index=initial_day,
        current_day_index=current_day,
        model_calls=model_calls,
        executions=executions,
        last_observation_id=observation_id,
        message=message,
    )


def _compact_execution(result: Any) -> dict[str, Any]:
    final = result.final_observation
    return {
        "execution_id": result.execution_id,
        "plan_id": result.plan_id,
        "status": result.status,
        "message": result.message,
        "requires_replan": result.requires_replan,
        "actions": [
            {
                "action_id": action.get("action_id"),
                "action": action.get("action"),
                "target_id": action.get("target_id"),
                "status": action.get("status"),
                "code": action.get("code"),
                "message": action.get("message"),
                "retryable": action.get("retryable"),
            }
            for action in result.actions
        ],
        "final": (
            {
                "observation_id": final.observation_id,
                "location": final.game.location,
                "time": final.game.time,
                "day": final.game.day,
                "season": final.game.season,
                "money": final.player.money,
                "summary": final.summary.model_dump(mode="json"),
            }
            if final is not None
            else None
        ),
    }

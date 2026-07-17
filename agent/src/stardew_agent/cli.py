from __future__ import annotations

import argparse
import asyncio
import json
import os
from pathlib import Path

from dotenv import load_dotenv

from .orchestration import run_goal, water_one
from .providers import OpenAIPlanProvider
from .replay import replay_observation
from .session import SessionStore
from .validation import PlanValidator, load_json_strict


def parser() -> argparse.ArgumentParser:
    result = argparse.ArgumentParser(prog="stardew-agent")
    commands = result.add_subparsers(dest="command", required=True)
    validate = commands.add_parser("validate", help="Validate an action-plan JSON file.")
    validate.add_argument("plan", type=Path)
    run = commands.add_parser("water-one", help="Run the deterministic one-crop slice.")
    run.add_argument("--endpoint", required=True, type=Path)
    run.add_argument("--runs", type=Path, default=Path("runs"))
    autonomous = commands.add_parser("run-goal", help="Run a bounded autonomous crop goal.")
    autonomous.add_argument("goal")
    autonomous.add_argument("--endpoint", required=True, type=Path)
    autonomous.add_argument("--runs", type=Path, default=Path("runs"))
    resume = commands.add_parser("resume", help="Resume a saved autonomous session.")
    resume.add_argument("session", type=Path)
    resume.add_argument("--endpoint", required=True, type=Path)
    resume.add_argument("--runs", type=Path, default=Path("runs"))
    status = commands.add_parser("status", help="Show the latest saved runner checkpoint.")
    status.add_argument("--session", type=Path)
    status.add_argument("--runs", type=Path, default=Path("runs"))
    replay = commands.add_parser(
        "replay-observation", help="Plan from a recorded observation without the game."
    )
    replay.add_argument("observation", type=Path)
    return result


def main() -> int:
    args = parser().parse_args()
    if args.command == "validate":
        validation = PlanValidator().validate(load_json_strict(args.plan))
        if validation.valid:
            print(json.dumps({"valid": True}))
            return 0
        print(
            json.dumps(
                {"valid": False, "issues": [issue.__dict__ for issue in validation.issues]},
                indent=2,
            )
        )
        return 1
    if args.command == "water-one":
        return asyncio.run(water_one(args.endpoint, args.runs))
    if args.command in {"run-goal", "resume"}:
        load_dotenv()
        api_key = os.environ.get("OPENAI_API_KEY")
        if not api_key:
            print("OPENAI_API_KEY is missing. Add it to .env before starting the runner.")
            return 2
        provider = OpenAIPlanProvider(
            api_key=api_key,
            model=os.environ.get("OPENAI_MODEL", "gpt-5.6-terra"),
        )
        resume_path = args.session if args.command == "resume" else None
        goal = args.goal if args.command == "run-goal" else "resume"
        return asyncio.run(
            run_goal(
                goal_text=goal,
                endpoint=args.endpoint,
                runs_root=args.runs,
                provider=provider,
                resume_path=resume_path,
            )
        )
    if args.command == "status":
        session = args.session or SessionStore.latest(args.runs)
        if session is None:
            print(json.dumps({"status": "NO_SESSION"}))
            return 1
        checkpoint = SessionStore.read_checkpoint_file(session)
        print(json.dumps({"session": str(session), **checkpoint}, indent=2))
        return 0
    if args.command == "replay-observation":
        print(json.dumps(replay_observation(args.observation), indent=2, sort_keys=True))
        return 0
    raise AssertionError("unreachable")


if __name__ == "__main__":
    raise SystemExit(main())

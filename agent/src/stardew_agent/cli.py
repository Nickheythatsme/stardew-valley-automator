from __future__ import annotations

import argparse
import asyncio
import json
from pathlib import Path

from .orchestration import water_one
from .replay import replay_observation
from .validation import PlanValidator, load_json_strict


def parser() -> argparse.ArgumentParser:
    result = argparse.ArgumentParser(prog="stardew-agent")
    commands = result.add_subparsers(dest="command", required=True)
    validate = commands.add_parser("validate", help="Validate an action-plan JSON file.")
    validate.add_argument("plan", type=Path)
    run = commands.add_parser("water-one", help="Run the deterministic one-crop slice.")
    run.add_argument("--endpoint", required=True, type=Path)
    run.add_argument("--runs", type=Path, default=Path("runs"))
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
    if args.command == "replay-observation":
        print(json.dumps(replay_observation(args.observation), indent=2, sort_keys=True))
        return 0
    raise AssertionError("unreachable")


if __name__ == "__main__":
    raise SystemExit(main())

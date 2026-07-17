from __future__ import annotations

import re

from .models import GoalSpec, Observation

_MONEY = re.compile(r"(?:\$|usd\s*)?([0-9][0-9,]*)", re.IGNORECASE)


class UnsupportedGoalError(ValueError):
    pass


def compile_goal(goal: str) -> GoalSpec:
    normalized = goal.strip()
    lowered = normalized.lower()
    if not any(word in lowered for word in ("crop", "plant", "harvest")):
        raise UnsupportedGoalError("The first autonomous goal family requires crop farming.")
    if not any(word in lowered for word in ("money", "wallet", "bank", "$")):
        raise UnsupportedGoalError("The goal must specify a wallet-money threshold.")
    match = _MONEY.search(normalized)
    if match is None:
        raise UnsupportedGoalError("The goal does not contain a numeric money threshold.")
    target = int(match.group(1).replace(",", ""))
    return GoalSpec(
        type="wallet_at_least",
        target_money=target,
        income_domain="crops",
        original_goal=normalized,
    )


def goal_completed(goal: GoalSpec, observation: Observation) -> bool:
    return observation.player.money >= goal.target_money


def game_day_index(observation: Observation) -> int:
    season_index = {"spring": 0, "summer": 1, "fall": 2, "winter": 3}[
        observation.game.season
    ]
    return (observation.game.year - 1) * 112 + season_index * 28 + observation.game.day

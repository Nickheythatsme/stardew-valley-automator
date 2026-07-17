import pytest

from stardew_agent.goals import compile_goal, goal_completed


def test_compiles_crop_wallet_goal() -> None:
    goal = compile_goal("Plant and harvest crops until you have $1,000 in the bank")
    assert goal.type == "wallet_at_least"
    assert goal.target_money == 1000
    assert goal.income_domain == "crops"


def test_rejects_unsupported_goal() -> None:
    with pytest.raises(ValueError, match="requires crop farming"):
        compile_goal("Become best friends with Leah")


def test_wallet_goal_uses_observed_money(observation) -> None:
    goal = compile_goal("Harvest crops until I have $640")
    assert goal_completed(goal, observation)

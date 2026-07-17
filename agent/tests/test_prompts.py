from stardew_agent.models import Observation
from stardew_agent.prompts import PromptBuilder


def test_prompt_contains_bounded_sections(observation: Observation) -> None:
    builder = PromptBuilder()
    prompt = builder.user_prompt(
        goal="Water one crop",
        observation=observation,
        actions={"water_crop": {}},
        remaining_budget={"actions": 2},
        recent_executions=[],
        memory=[],
    )
    assert "GOAL\nWater one crop" in prompt
    assert "CURRENT OBSERVATION" in prompt
    assert "Return one ACTION_PLAN_SCHEMA JSON object." in prompt
    assert "Never invent" in builder.system_prompt()

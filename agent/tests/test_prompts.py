from stardew_agent.orchestration import ACTION_CATALOG
from stardew_agent.prompts.builder import PromptBuilder


def test_bedtime_guidance_requires_going_home_and_confirming_sleep() -> None:
    prompt = PromptBuilder().system_prompt()

    assert "destination FarmHouse" in prompt
    assert 'selects "Yes"' in prompt
    assert "Never use wait_until 2600" in prompt
    assert "faints if still awake at 2:00 AM (2600)" in prompt
    assert "wake in their bed" in prompt
    assert "lose money" in prompt
    assert "use sleep when the day's work is done" in prompt
    assert "FarmHouse" in ACTION_CATALOG["sleep"]["guidance"]

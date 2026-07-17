import pytest
from pydantic import ValidationError

from stardew_agent.models import PlannerActionPlan


def _plan() -> dict:
    nullable_args = {
        "target_id": None,
        "target_revision": None,
        "destination": None,
        "qualified_item_id": None,
        "offer_id": None,
        "offer_revision": None,
        "quantity": None,
        "source_id": None,
        "water_after_planting": None,
        "selector_item_ids": None,
        "category": None,
        "time": None,
        "response_id": None,
        "reason": "done",
    }
    return {
        "schema_version": "2.0",
        "plan_id": "provider-plan",
        "expected_observation_id": "obs-fixture-1",
        "goal": "Harvest crops until I have $1000",
        "actions": [
            {
                "action_id": "finish-1",
                "action": "finish",
                "args": nullable_args,
                "continue_on": [],
            }
        ],
        "stop_conditions": {
            "energy_below": 25,
            "game_time_at_or_after": 2500,
            "max_failures": 1,
        },
        "request_replan_after": True,
    }


def test_provider_dto_strips_null_arguments() -> None:
    protocol = PlannerActionPlan.model_validate(_plan()).to_protocol_plan()
    assert protocol.actions[0].args == {"reason": "done"}


def test_provider_dto_requires_nullable_fields_to_be_present() -> None:
    raw = _plan()
    del raw["actions"][0]["args"]["destination"]
    with pytest.raises(ValidationError):
        PlannerActionPlan.model_validate(raw)

from stardew_agent.agent_planner import AgentPlanner
from stardew_agent.models import Observation
from stardew_agent.planning import plan_water_one


def test_selects_reachable_dry_crop(observation: Observation) -> None:
    plan = plan_water_one(observation)
    assert plan.expected_observation_id == observation.observation_id
    assert plan.actions[0].action == "water_crop"
    assert plan.actions[0].args["target_id"] == "Farm/crop/43,19"


def test_finishes_when_no_crop_needs_water(observation: Observation) -> None:
    observation.entities[0].properties["watered"] = True
    observation.entities[0].properties["needs_watering"] = False
    plan = plan_water_one(observation)
    assert [action.action for action in plan.actions] == ["finish"]


def test_binds_selected_target_to_expected_snapshot_revision(
    observation: Observation,
) -> None:
    plan = plan_water_one(observation)
    plan.actions[0].args["target_revision"] = "model-copy-error"

    AgentPlanner._bind_snapshot_revisions(plan, observation)

    assert plan.actions[0].args["target_revision"] == "fixture-rev-1"

from __future__ import annotations

import json
from pathlib import Path

from stardew_agent.models import Observation
from stardew_agent.validation import PlanValidator


def test_valid_fixture(root: Path, observation: Observation) -> None:
    raw = json.loads((root / "fixtures" / "plans" / "water-one.json").read_text())
    result = PlanValidator().validate(raw, observation)
    assert result.valid
    assert result.plan is not None


def test_rejects_unknown_action(root: Path) -> None:
    raw = json.loads((root / "fixtures" / "plans" / "water-one.json").read_text())
    raw["actions"][0]["action"] = "shell"
    result = PlanValidator().validate(raw)
    assert not result.valid
    assert any(issue.code == "SCHEMA_VALIDATION_FAILED" for issue in result.issues)


def test_rejects_stale_target(root: Path, observation: Observation) -> None:
    raw = json.loads((root / "fixtures" / "plans" / "water-one.json").read_text())
    raw["actions"][0]["args"]["target_revision"] = "stale"
    result = PlanValidator().validate(raw, observation)
    assert not result.valid
    assert any(issue.code == "TARGET_STALE" for issue in result.issues)


def test_rejects_duplicate_action_ids(root: Path, observation: Observation) -> None:
    raw = json.loads((root / "fixtures" / "plans" / "water-one.json").read_text())
    raw["actions"][1]["action_id"] = raw["actions"][0]["action_id"]
    result = PlanValidator().validate(raw, observation)
    assert not result.valid
    assert any(issue.code == "DUPLICATE_ACTION_ID" for issue in result.issues)


def test_observation_accepts_omitted_nullable_fields(observation: Observation) -> None:
    raw = observation.model_dump(mode="json")
    raw["game"].pop("active_menu")
    for entity in raw["entities"]:
        if entity["path_cost"] is None:
            entity.pop("path_cost")
        for interaction in entity["interaction_tiles"]:
            if interaction["path_cost"] is None:
                interaction.pop("path_cost")

    parsed = Observation.model_validate(raw)

    assert parsed.game.active_menu is None

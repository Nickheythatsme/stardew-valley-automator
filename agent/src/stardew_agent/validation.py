from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path
from typing import Any

from jsonschema import Draft202012Validator, FormatChecker

from .models import ActionPlan, Observation


@dataclass(frozen=True)
class ValidationIssue:
    path: str
    code: str
    message: str


@dataclass(frozen=True)
class ValidationResult:
    valid: bool
    issues: tuple[ValidationIssue, ...]
    plan: ActionPlan | None = None


def repository_root() -> Path:
    return Path(__file__).resolve().parents[3]


class PlanValidator:
    def __init__(self, schema_path: Path | None = None) -> None:
        path = schema_path or repository_root() / "schemas" / "action-plan.schema.json"
        self.schema = json.loads(path.read_text(encoding="utf-8"))
        self.validator = Draft202012Validator(self.schema, format_checker=FormatChecker())

    def validate(
        self, raw_plan: dict[str, Any], observation: Observation | None = None
    ) -> ValidationResult:
        issues = [
            ValidationIssue(
                path="/" + "/".join(str(part) for part in error.absolute_path),
                code="SCHEMA_VALIDATION_FAILED",
                message=error.message,
            )
            for error in sorted(
                self.validator.iter_errors(raw_plan), key=lambda err: list(err.absolute_path)
            )
        ]
        plan: ActionPlan | None = None
        if not issues:
            plan = ActionPlan.model_validate(raw_plan)
            issues.extend(self._semantic_issues(plan, observation))
        return ValidationResult(not issues, tuple(issues), plan if not issues else None)

    @staticmethod
    def _semantic_issues(
        plan: ActionPlan, observation: Observation | None
    ) -> list[ValidationIssue]:
        issues: list[ValidationIssue] = []
        ids: set[str] = set()
        for index, action in enumerate(plan.actions):
            if action.action_id in ids:
                issues.append(
                    ValidationIssue(
                        f"/actions/{index}/action_id",
                        "DUPLICATE_ACTION_ID",
                        "Action IDs must be unique.",
                    )
                )
            ids.add(action.action_id)

        if observation is None:
            return issues
        if plan.expected_observation_id != observation.observation_id:
            issues.append(
                ValidationIssue(
                    "/expected_observation_id",
                    "STALE_OBSERVATION",
                    "The plan does not target the current observation.",
                )
            )
            return issues

        entities = {entity.id: entity for entity in observation.entities}
        for index, action in enumerate(plan.actions):
            if action.action in {"clear_debris", "plant_crop", "water_crop", "harvest_crop"}:
                entity = entities.get(action.args["target_id"])
                if entity is None:
                    issues.append(
                        ValidationIssue(
                            f"/actions/{index}/args/target_id",
                            "TARGET_NOT_FOUND",
                            "The target does not exist in the observation.",
                        )
                    )
                elif entity.revision != action.args["target_revision"]:
                    issues.append(
                        ValidationIssue(
                            f"/actions/{index}/args/target_revision",
                            "TARGET_STALE",
                            "The target revision does not match the observation.",
                        )
                    )
                elif action.action in {"water_crop", "harvest_crop"} and entity.kind != "crop":
                    issues.append(
                        ValidationIssue(
                            f"/actions/{index}/args/target_id",
                            "WRONG_TARGET_KIND",
                            "The selected action requires a crop target.",
                        )
                    )
                elif action.action == "plant_crop" and entity.kind != "planting_tile":
                    issues.append(
                        ValidationIssue(
                            f"/actions/{index}/args/target_id",
                            "WRONG_TARGET_KIND",
                            "plant_crop requires a planting_tile target.",
                        )
                    )
                elif action.action == "clear_debris" and entity.kind != "debris":
                    issues.append(
                        ValidationIssue(
                            f"/actions/{index}/args/target_id",
                            "WRONG_TARGET_KIND",
                            "clear_debris requires a debris target.",
                        )
                    )
            if action.action == "buy_item":
                offers = {offer.id: offer for offer in observation.shop_offers}
                offer = offers.get(action.args["offer_id"])
                if offer is None:
                    issues.append(
                        ValidationIssue(
                            f"/actions/{index}/args/offer_id",
                            "OFFER_NOT_FOUND",
                            "The shop offer is not present in the observation.",
                        )
                    )
                elif offer.revision != action.args["offer_revision"]:
                    issues.append(
                        ValidationIssue(
                            f"/actions/{index}/args/offer_revision",
                            "OFFER_STALE",
                            "The shop offer revision does not match.",
                        )
                    )
        return issues


def load_json_strict(path: Path) -> dict[str, Any]:
    def reject_duplicates(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
        result: dict[str, Any] = {}
        for key, value in pairs:
            if key in result:
                raise ValueError(f"Duplicate JSON key: {key}")
            result[key] = value
        return result

    with path.open(encoding="utf-8") as handle:
        return json.load(handle, object_pairs_hook=reject_duplicates)

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
            if action.action in {"water_crop", "harvest_crop"}:
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
                elif entity.kind != "crop":
                    issues.append(
                        ValidationIssue(
                            f"/actions/{index}/args/target_id",
                            "WRONG_TARGET_KIND",
                            "The selected action requires a crop target.",
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

from __future__ import annotations

import json
from pathlib import Path

import pytest

from stardew_agent.models import Observation


@pytest.fixture
def root() -> Path:
    return Path(__file__).resolve().parents[2]


@pytest.fixture
def observation(root: Path) -> Observation:
    raw = json.loads((root / "fixtures" / "observations" / "dry-crop.json").read_text())
    return Observation.model_validate(raw)

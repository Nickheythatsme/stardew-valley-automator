from __future__ import annotations

import gzip
import json
from pathlib import Path

from .models import Observation
from .planning import plan_water_one


def replay_observation(path: Path) -> dict[str, object]:
    opener = gzip.open if path.suffix == ".gz" else path.open
    if path.suffix == ".gz":
        with opener(path, "rt", encoding="utf-8") as handle:
            raw = json.load(handle)
    else:
        with opener(encoding="utf-8") as handle:
            raw = json.load(handle)
    observation = Observation.model_validate(raw)
    return plan_water_one(observation).model_dump(mode="json", exclude_none=True)

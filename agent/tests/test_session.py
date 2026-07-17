from __future__ import annotations

import gzip
import json
from pathlib import Path

from stardew_agent.models import Observation
from stardew_agent.session import SessionStore


def test_session_writes_compressed_observation(tmp_path: Path, observation: Observation) -> None:
    store = SessionStore(tmp_path, "test goal")
    try:
        digest = store.observation(observation)
        assert len(digest) == 64
        path = store.path / "observations" / f"{observation.observation_id}.json.gz"
        with gzip.open(path, "rt", encoding="utf-8") as handle:
            assert json.load(handle)["observation_id"] == observation.observation_id
    finally:
        store.close()

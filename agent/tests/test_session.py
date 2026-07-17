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


def test_session_writes_llm_debug_jsonl(tmp_path: Path) -> None:
    store = SessionStore(tmp_path, "test goal")
    try:
        store.llm_debug(
            {
                "event": "request",
                "system_prompt": "bounded system prompt",
                "user_prompt": "bounded user prompt",
            }
        )
        record = json.loads((store.path / "llm-debug.jsonl").read_text(encoding="utf-8"))
        assert record["event"] == "request"
        assert record["user_prompt"] == "bounded user prompt"
    finally:
        store.close()

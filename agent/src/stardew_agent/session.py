from __future__ import annotations

import gzip
import hashlib
import json
import sqlite3
import uuid
from datetime import UTC, datetime
from pathlib import Path
from typing import Any

from pydantic import BaseModel


def _jsonable(value: Any) -> Any:
    if isinstance(value, BaseModel):
        return value.model_dump(mode="json")
    return value


class SessionStore:
    def __init__(self, root: Path, goal: str) -> None:
        self.session_id = f"session-{uuid.uuid4()}"
        now = datetime.now(UTC)
        self.path = root / now.date().isoformat() / self.session_id
        self.path.mkdir(parents=True, exist_ok=False)
        (self.path / "observations").mkdir()
        (self.path / "plans").mkdir()
        metadata = {
            "session_id": self.session_id,
            "goal": goal,
            "started_at_utc": now.isoformat(),
            "schema_version": "1.0",
        }
        (self.path / "session.json").write_text(
            json.dumps(metadata, indent=2, sort_keys=True), encoding="utf-8"
        )
        self.database = sqlite3.connect(root / "agent.db")
        self.database.execute(
            """
            CREATE TABLE IF NOT EXISTS sessions (
                session_id TEXT PRIMARY KEY,
                goal TEXT NOT NULL,
                started_at_utc TEXT NOT NULL,
                path TEXT NOT NULL
            )
            """
        )
        self.database.execute(
            "INSERT INTO sessions VALUES (?, ?, ?, ?)",
            (self.session_id, goal, now.isoformat(), str(self.path)),
        )
        self.database.commit()

    def close(self) -> None:
        self.database.close()

    def append(self, stream: str, value: Any) -> None:
        payload = json.dumps(_jsonable(value), separators=(",", ":"), sort_keys=True)
        with (self.path / f"{stream}.jsonl").open("a", encoding="utf-8") as handle:
            handle.write(payload + "\n")

    def observation(self, observation: BaseModel) -> str:
        payload = json.dumps(
            observation.model_dump(mode="json"), separators=(",", ":"), sort_keys=True
        )
        observation_id = observation.model_dump()["observation_id"]
        destination = self.path / "observations" / f"{observation_id}.json.gz"
        with gzip.open(destination, "wt", encoding="utf-8") as handle:
            handle.write(payload)
        return hashlib.sha256(payload.encode()).hexdigest()

    def plan(self, turn: int, plan: BaseModel) -> None:
        destination = self.path / "plans" / f"turn-{turn:04d}.json"
        destination.write_text(
            json.dumps(
                plan.model_dump(mode="json", exclude_none=True), indent=2, sort_keys=True
            ),
            encoding="utf-8",
        )

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
            "schema_version": "2.0",
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

    @classmethod
    def resume(cls, root: Path, session_path: Path) -> SessionStore:
        metadata_path = session_path / "session.json"
        metadata = json.loads(metadata_path.read_text(encoding="utf-8"))
        instance = cls.__new__(cls)
        instance.session_id = str(metadata["session_id"])
        instance.path = session_path
        instance.database = sqlite3.connect(root / "agent.db")
        return instance

    @staticmethod
    def latest(root: Path) -> Path | None:
        sessions = [
            path.parent
            for path in root.glob("????-??-??/session-*/session.json")
            if path.is_file()
        ]
        return max(sessions, key=lambda path: path.stat().st_mtime) if sessions else None

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

    def provider_call(self, value: dict[str, Any]) -> None:
        self.append("provider", value)

    def llm_debug(self, value: dict[str, Any]) -> None:
        self.append("llm-debug", value)

    def checkpoint(self, value: BaseModel | dict[str, Any]) -> None:
        payload = _jsonable(value)
        (self.path / "checkpoint.json").write_text(
            json.dumps(payload, indent=2, sort_keys=True), encoding="utf-8"
        )

    def read_checkpoint(self) -> dict[str, Any]:
        return json.loads((self.path / "checkpoint.json").read_text(encoding="utf-8"))

    @staticmethod
    def read_checkpoint_file(session_path: Path) -> dict[str, Any]:
        return json.loads((session_path / "checkpoint.json").read_text(encoding="utf-8"))

    def finish(self, status: str, message: str) -> None:
        path = self.path / "session.json"
        metadata = json.loads(path.read_text(encoding="utf-8"))
        metadata["status"] = status
        metadata["message"] = message
        metadata["ended_at_utc"] = datetime.now(UTC).isoformat()
        path.write_text(json.dumps(metadata, indent=2, sort_keys=True), encoding="utf-8")

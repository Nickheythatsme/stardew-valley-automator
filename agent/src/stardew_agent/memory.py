from __future__ import annotations

import json
import sqlite3
from datetime import UTC, datetime
from pathlib import Path
from typing import Any

from .models import Observation


class EpisodicMemory:
    """Stores only facts derived from verified final observations."""

    def __init__(self, path: Path) -> None:
        self.connection = sqlite3.connect(path)
        self.connection.row_factory = sqlite3.Row
        self.connection.execute(
            """
            CREATE TABLE IF NOT EXISTS episodic_facts (
                save_id TEXT NOT NULL,
                location TEXT NOT NULL,
                entity_id TEXT NOT NULL,
                revision TEXT NOT NULL,
                fact_json TEXT NOT NULL,
                verified_at_utc TEXT NOT NULL,
                PRIMARY KEY (save_id, location, entity_id)
            )
            """
        )
        self.connection.commit()

    def close(self) -> None:
        self.connection.close()

    def update_from_verified_observation(self, observation: Observation) -> None:
        now = datetime.now(UTC).isoformat()
        for entity in observation.entities:
            if entity.kind not in {"container", "water_source"}:
                continue
            fact = {
                "entity_id": entity.id,
                "kind": entity.kind,
                "tile": entity.tile.model_dump(),
                "reachable": entity.reachable,
                "properties": entity.properties,
            }
            self.connection.execute(
                """
                INSERT INTO episodic_facts VALUES (?, ?, ?, ?, ?, ?)
                ON CONFLICT(save_id, location, entity_id) DO UPDATE SET
                    revision = excluded.revision,
                    fact_json = excluded.fact_json,
                    verified_at_utc = excluded.verified_at_utc
                """,
                (
                    observation.game.save_id,
                    observation.game.location,
                    entity.id,
                    entity.revision,
                    json.dumps(fact, separators=(",", ":"), sort_keys=True),
                    now,
                ),
            )
        self.connection.commit()

    def relevant(self, save_id: str, location: str, limit: int = 32) -> list[dict[str, Any]]:
        rows = self.connection.execute(
            """
            SELECT fact_json FROM episodic_facts
            WHERE save_id = ? AND location = ?
            ORDER BY verified_at_utc DESC, entity_id
            LIMIT ?
            """,
            (save_id, location, limit),
        )
        return [json.loads(row["fact_json"]) for row in rows]

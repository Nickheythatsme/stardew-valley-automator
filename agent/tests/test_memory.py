from pathlib import Path

from stardew_agent.memory import EpisodicMemory
from stardew_agent.models import Observation


def test_memory_only_keeps_durable_entity_facts(tmp_path: Path, observation: Observation) -> None:
    observation.entities[0].kind = "water_source"
    memory = EpisodicMemory(tmp_path / "memory.db")
    try:
        memory.update_from_verified_observation(observation)
        facts = memory.relevant("test-save", "Farm")
        assert facts[0]["entity_id"] == observation.entities[0].id
    finally:
        memory.close()

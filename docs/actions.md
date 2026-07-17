# Actions

Actions are verified transactions: preconditions, preparation, movement, facing, input, completion wait, postcondition, result.

Implemented:

- `move_to`: four-directional A* to a centered Farm tile; warps are forbidden.
- `water_crop`: resolve exact crop ID/revision, move to the best adjacent tile, select the watering can, face the crop, trigger the configured tool input, and require dry-to-watered state.
- `refill_watering_can`: select a reachable observed water edge and require the can's water level to increase.
- `wait`: yield for 1–300 update ticks.
- `finish`: end the plan explicitly.

`harvest_crop`, `plant_seed`, and `deposit_items` are represented in the shared v1 schema for forward compatibility but currently return `ACTION_NOT_IMPLEMENTED`. They are not advertised in the mod's `hello.capabilities` list.

Every result includes timestamps, game time, checked preconditions, retryability, trace states, and—for state-changing implemented actions—a structured state diff.

# Actions

Actions are verified transactions: preconditions, preparation, movement, facing, input, completion wait, postcondition, result.

Implemented:

- `travel_to`: follows one observed, expected warp edge between supported locations.
- `clear_debris`: selects the observed required tool and verifies the debris disappeared.
- `plant_crop`: tills if needed, plants an exact qualified seed ID, optionally waters,
  and verifies soil, inventory, crop, and water changes.
- `water_crop`, `refill_watering_can`, and hand `harvest_crop`: navigate, interact,
  and verify their semantic postconditions.
- `buy_item`: uses an observed, revisioned visible vanilla shop offer and verifies both
  inventory and wallet deltas.
- `ship_items`: opens the observed shipping bin, transfers matching items through the
  vanilla menu, and verifies inventory and bin deltas.
- `wait_until`, `sleep`, `advance_dialogue`, and `dismiss_menu`: bounded vanilla time
  and owned-UI transitions.
- `finish`: ends the plan; only the Python goal evaluator can declare the overall
  wallet goal complete.
The schema also reserves `choose_response`; it is not advertised until exact question
choice extraction is supported. Unknown or modded menus interrupt execution.

Every result includes timestamps, game time, checked preconditions, retryability, trace states, and—for state-changing implemented actions—a structured state diff.

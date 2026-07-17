# Architecture

The prototype has one strict trust boundary: the SMAPI mod is authoritative for all gameplay. The Python process may propose plans, but the mod independently validates targets, budgets, location, action names, and live preconditions.

The C# projects are separated into:

- `StardewAgent.Protocol`: transport-neutral immutable DTOs, limits, JSON settings, and plan validation;
- `StardewAgent.Core`: game-independent navigation and execution-budget logic;
- `StardewAgent.Mod`: direct SMAPI/Stardew access, observations, socket bridge, movement, and actions.

Socket tasks exchange immutable requests through a concurrent queue. Only `UpdateTicked` reads game objects or advances actions. `UpdateTicking` injects a single configured game input for the current movement/tool state. Disconnects, manual input, warps, unexpected menus, timeouts, and budget violations terminate the active execution.

The v2 live slice includes structured multi-location observations and bounded
crop-economy transactions. The Python process owns OpenAI calls, goal evaluation,
checkpoints, and replanning. The mod remains the safety boundary and owns navigation,
vanilla UI transitions, postcondition checks, cancellation, and execution budgets.

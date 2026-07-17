# Stardew Valley Automator

A safety-first, mod-based control layer for an AI agent playing Stardew Valley. The system is split into:

- a C# SMAPI mod that owns observation, navigation, semantic actions, verification, budgets, and cancellation;
- a Python runtime that validates JSON action plans, supervises execution, logs sessions, evaluates goals, and calls OpenAI through a provider-neutral interface;
- canonical JSON Schemas shared by both processes.

The v2 prototype supports bounded crop-economy goals across Farm, FarmHouse, BusStop,
Town, and SeedShop. The Python runner uses OpenAI Structured Outputs to create JSON
action plans; the SMAPI mod independently validates and verifies each gameplay action.
Generated code is never executed.

## Run an autonomous goal

1. Start Stardew Valley through SMAPI, load the save you intentionally want to automate,
   and leave the game running.
2. Open a second terminal in this repository and install/update the runner:

   ```sh
   uv sync --project agent --all-extras
   ```

3. Find the endpoint written by the running mod:

   ```sh
   ENDPOINT="$HOME/Library/Application Support/Steam/steamapps/common/Stardew Valley/Contents/MacOS/Mods/StardewAgent.Mod/.runtime/endpoint.json"
   ```

4. Start the bounded autonomous crop goal:

   ```sh
   uv run --project agent stardew-agent run-goal \
     "Plant and harvest crops until you have $1000 in the bank" \
     --endpoint "$ENDPOINT"
   ```

The runner reads `OPENAI_API_KEY` and optional `OPENAI_MODEL` from `.env`. Its defaults
are model `gpt-5.6-terra`, medium reasoning, 14 in-game days, 64 provider requests, and
120 wall-clock minutes. Manual movement/tool/action input cancels automation.

Each autonomous session prints the path to `llm-debug.jsonl`. That file records the
exact system/user prompts sent to OpenAI, parsed structured responses, normalized plans,
validation issues, retries, response IDs, and token usage. It never records the API key.
Monitor the newest session with:

```sh
tail -f "$(find runs -name llm-debug.jsonl -type f -print0 | xargs -0 ls -t | head -1)" \
  | jq .
```

Disable **Pause When Game Window Is Inactive** in Stardew's options if you want to
watch this log in another Terminal while automation runs. Otherwise the runner waits
without sending model requests until the game is focused again.

Inspect or resume the latest checkpoint with:

```sh
uv run --project agent stardew-agent status
uv run --project agent stardew-agent resume runs/YYYY-MM-DD/session-UUID \
  --endpoint "$ENDPOINT"
```

The deterministic regression command remains available:

```sh
uv run --project agent stardew-agent water-one --endpoint "$ENDPOINT"
```

## Run the automator end to end on this Mac

The deterministic `water-one` regression observes structured game state, optionally
refills an empty watering can, waters one crop, verifies the change, and records it.

### 1. One-time setup

The local machine is already configured with Stardew Valley 1.6.15, SMAPI 4.5.2, Stardew Agent 0.1.0, a local .NET SDK, `uv`, and the Python virtual environment. To recreate or refresh only the Python environment, run:

```sh
cd "/Users/nickgrout/Documents/stardew-vally-automator"
uv sync --project agent --all-extras
```

The installed mod is located at:

```text
/Users/nickgrout/Library/Application Support/Steam/steamapps/common/Stardew Valley/Contents/MacOS/Mods/StardewAgent.Mod
```

### 2. Prepare a disposable test farm

Do not begin with an important save. Create a new disposable Standard Farm or use a copied test save. Before running the agent, make sure:

- the player is standing on the Farm;
- it is before 12:00 PM and the player has more than 25 energy;
- at least one planted crop is dry and has a clear approach tile;
- the watering can is in the player inventory;
- no menu, dialogue, cutscene, or festival is active; and
- **Pause When Game Window Is Inactive** is disabled in Stardew Valley's options. Otherwise the game may pause while the Python command has focus.

### 3. Start Stardew Valley through Steam

Open Steam and click **Play** for Stardew Valley. The repaired macOS launcher opens a persistent SMAPI Terminal console, then starts the game. In that console, confirm these messages appear:

```text
Stardew Agent 0.1.0
Mods loaded and ready!
Stardew Agent protocol server listening on 127.0.0.1:...
```

The port and authentication token change every launch. The mod writes their discovery file automatically; you never need to copy either value from the console.

### 4. Load the test farm and run the read-only probe

Load the disposable save and remain on the Farm. In the SMAPI Terminal console, enter:

```text
stardew_agent_probe
```

The probe should report `location=Farm`, a valid player tile, `watering_can_slot` other than `-1`, and the nearby crop counts. Return focus to the game with Command–Tab.

### 5. Start the one-crop automator

Open a second Terminal window and run:

```sh
cd "/Users/nickgrout/Documents/stardew-vally-automator"

STARDEW_AGENT_ENDPOINT="/Users/nickgrout/Library/Application Support/Steam/steamapps/common/Stardew Valley/Contents/MacOS/Mods/StardewAgent.Mod/.runtime/endpoint.json"

test -f "$STARDEW_AGENT_ENDPOINT" && echo "SMAPI endpoint ready"

agent/.venv/bin/stardew-agent water-one \
  --endpoint "$STARDEW_AGENT_ENDPOINT" \
  --runs runs
```

Once it connects, return focus to Stardew Valley with Command–Tab. Do not press movement, action, or tool buttons while the plan is running; manual input intentionally cancels automation.

The command exits with status `0` after a verified success. It may currently finish without console output, so the authoritative result is the game state plus the recorded execution file described below. If no reachable dry crop exists, the generated plan safely finishes without using the watering can.

### 6. Inspect the result and replay artifacts

Each attempt creates a directory under:

```text
runs/YYYY-MM-DD/session-UUID/
```

The directory contains:

- `session.json` with the goal and session identity;
- `plans/turn-0001.json` with the exact accepted plan;
- `executions.jsonl` with action results, failure codes, state diffs, and the final observation; and
- `observations/*.json.gz` with the before/after structured game state.

The SQLite session index is written to `runs/agent.db`. These runtime files are ignored by Git.

### 7. Stop safely

To interrupt an active run, press a movement, action, or tool button in the game, or press Control–C in the Python Terminal. The mod releases injected input and cancels the execution when the supervising client disconnects. Close Stardew Valley normally when finished; the per-launch endpoint file is removed during clean shutdown.

### Troubleshooting

| Symptom | Resolution |
|---|---|
| SMAPI Terminal does not open | Launch from Steam, not the original game executable. The installed wrapper uses a persistent `open-smapi-terminal.command` beside SMAPI. |
| `SMAPI endpoint ready` is not printed | Confirm Stardew Agent appears in the SMAPI loaded-mod list. Restart the game if `.runtime/endpoint.json` is stale or absent. |
| `WORLD_NOT_READY` | Load a save fully before starting Python. |
| `UNSUPPORTED_LOCATION` | Return to Farm, FarmHouse, BusStop, Town, or SeedShop. |
| The player does not move while Terminal is focused | Disable **Pause When Game Window Is Inactive**, then Command–Tab back to the game. |
| The plan finishes without watering | Make sure a reachable crop is dry, it is before noon, energy is above 25, and the watering can is present. |
| `CLIENT_ALREADY_CONNECTED` | Stop the earlier `stardew-agent` process or restart Stardew Valley. |
| The run is cancelled immediately | Avoid movement/tool/action input and close any open game menu before starting. |

## Validate the local installation

Run the deterministic plan validator and test suites without opening the game:

```sh
cd "/Users/nickgrout/Documents/stardew-vally-automator"
uv run --project agent stardew-agent validate fixtures/plans/water-one.json
uv run --project agent pytest -q
```

## C# development

```sh
DOTNET_CLI_HOME="$PWD/.dotnet-home" .dotnet/dotnet test StardewAgent.sln
DOTNET_CLI_HOME="$PWD/.dotnet-home" .dotnet/dotnet build src/StardewAgent.Mod/StardewAgent.Mod.csproj
```

`Pathoschild.Stardew.ModBuildConfig` locates the installed game and copies the built mod into the SMAPI Mods directory. The pure protocol/core tests do not require the game.

The repository pins SDK 8.0.419 because ModBuildConfig 4.4's analyzer requires its newer C# compiler. All shipped C# assemblies still target `net6.0` for Stardew compatibility.


## Safety properties

- The server binds only to `127.0.0.1` on an ephemeral port.
- A new 256-bit authentication token is generated per mod launch.
- Only one client is accepted at a time.
- Background socket tasks never access Stardew game objects.
- The mod validates all action plans independently of Python.
- No generated Python, Lua, C#, reflection, Harmony patch, subprocess, or host callback is executed.
- Manual player input, unexpected menus, location changes, timeouts, and disconnects stop automation.
- Successful actions require verified state changes; animations alone do not count.

## Live-game compatibility gate

Before using a normal save, use a copied test save and run the SMAPI console command:

```text
stardew_agent_probe
```

The command reports the game/SMAPI versions, location, player state, watering-can state, nearby crops, and representative collision results. The release build does not include any save-mutating scenario setup.

See [docs/architecture.md](docs/architecture.md), [docs/protocol.md](docs/protocol.md), [docs/actions.md](docs/actions.md), and [docs/testing.md](docs/testing.md).

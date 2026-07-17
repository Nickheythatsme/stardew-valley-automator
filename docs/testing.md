# Testing

Pure tests do not require Stardew Valley:

```sh
uv run --project agent pytest
DOTNET_CLI_HOME="$PWD/.dotnet-home" .dotnet/dotnet test StardewAgent.sln --no-build --no-restore -m:1 -nr:false
```

The Python suite covers typed goal compilation, strict schemas, OpenAI planner DTOs,
stale entities, duplicate IDs, deterministic planning, NDJSON request correlation,
prompt construction, session artifacts, and verified episodic memory.

The C# suite covers A* obstacles, unreachable targets, interaction ranking, execution lifecycle transitions, snake-case serialization, and stale-observation validation.

For live testing, follow the end-to-end runbook in the root `README.md`. Use a
disposable or copied save, disable pausing while the game is inactive, and run
`stardew_agent_probe` before the deterministic `water-one` regression or an autonomous
goal. Confirm cancellation and manual input safety first. Do not use Stardew debug
commands on a normal save.

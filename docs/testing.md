# Testing

Pure tests do not require Stardew Valley:

```sh
agent/.venv/bin/pytest
DOTNET_CLI_HOME="$PWD/.dotnet-home" .dotnet/dotnet test StardewAgent.sln --no-build --no-restore -m:1 -nr:false
```

The Python suite covers strict schemas, stale entities, duplicate IDs, deterministic planning, NDJSON request correlation, prompt construction, session artifacts, and verified episodic memory.

The C# suite covers A* obstacles, unreachable targets, interaction ranking, execution lifecycle transitions, snake-case serialization, and stale-observation validation.

For live testing, follow the machine-specific end-to-end runbook in the root `README.md`. Use a disposable or copied save on the Farm, disable pausing while the game is inactive, and run `stardew_agent_probe` before starting the deterministic Python `water-one` command. Confirm that cancellation and manual input are safe before trying a state-changing plan. Do not use Stardew debug commands on a normal save.

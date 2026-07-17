from __future__ import annotations

import asyncio
import contextlib
import json
import os
import uuid
from collections.abc import AsyncIterator
from pathlib import Path
from typing import Any

from .models import ActionPlan, EventEnvelope, ExecutionResult, Observation, ResponseEnvelope

PROTOCOL_VERSION = "1.0"
MAX_LINE_BYTES = 1024 * 1024


class GameProtocolError(RuntimeError):
    def __init__(self, code: str, message: str, details: Any = None) -> None:
        super().__init__(f"{code}: {message}")
        self.code = code
        self.details = details


class GameClient:
    def __init__(self, host: str, port: int, token: str) -> None:
        if host != "127.0.0.1":
            raise ValueError("The Stardew agent client only connects to IPv4 loopback.")
        self.host = host
        self.port = port
        self.token = token
        self._reader: asyncio.StreamReader | None = None
        self._writer: asyncio.StreamWriter | None = None
        self._reader_task: asyncio.Task[None] | None = None
        self._pending: dict[str, asyncio.Future[ResponseEnvelope]] = {}
        self._events: asyncio.Queue[EventEnvelope] = asyncio.Queue(maxsize=1024)
        self._write_lock = asyncio.Lock()

    @classmethod
    def from_endpoint_file(cls, path: Path) -> GameClient:
        endpoint = json.loads(path.read_text(encoding="utf-8"))
        if endpoint.get("protocol_version") != PROTOCOL_VERSION:
            raise ValueError("The endpoint uses an unsupported protocol version.")
        try:
            os.kill(int(endpoint["pid"]), 0)
        except ProcessLookupError as error:
            message = "The endpoint file is stale; its game process is no longer running."
            raise ValueError(message) from error
        except PermissionError:
            pass
        return cls(endpoint["host"], int(endpoint["port"]), endpoint["token"])

    async def connect(self) -> dict[str, Any]:
        self._reader, self._writer = await asyncio.wait_for(
            asyncio.open_connection(
                self.host,
                self.port,
                limit=MAX_LINE_BYTES + 1,
            ),
            timeout=5,
        )
        self._reader_task = asyncio.create_task(self._read_loop(), name="stardew-agent-reader")
        return await self.request(
            "hello",
            {"token": self.token, "client_name": "stardew-agent-python", "client_version": "0.1.0"},
            timeout=15,
        )

    async def close(self) -> None:
        if self._reader_task is not None:
            self._reader_task.cancel()
            with contextlib.suppress(asyncio.CancelledError):
                await self._reader_task
            self._reader_task = None
        if self._writer is not None:
            self._writer.close()
            await self._writer.wait_closed()
            self._writer = None
        error = GameProtocolError("DISCONNECTED", "The game client disconnected.")
        for future in self._pending.values():
            if not future.done():
                future.set_exception(error)
        self._pending.clear()

    async def __aenter__(self) -> GameClient:
        await self.connect()
        return self

    async def __aexit__(self, *_: object) -> None:
        await self.close()

    async def request(self, method: str, params: dict[str, Any], timeout: float = 10) -> Any:
        if self._writer is None:
            raise GameProtocolError("NOT_CONNECTED", "Connect before issuing requests.")
        request_id = f"req-{uuid.uuid4()}"
        loop = asyncio.get_running_loop()
        future: asyncio.Future[ResponseEnvelope] = loop.create_future()
        self._pending[request_id] = future
        envelope = {
            "protocol_version": PROTOCOL_VERSION,
            "type": "request",
            "request_id": request_id,
            "method": method,
            "params": params,
        }
        encoded = json.dumps(envelope, separators=(",", ":"), ensure_ascii=False).encode() + b"\n"
        if len(encoded) > MAX_LINE_BYTES:
            self._pending.pop(request_id, None)
            raise ValueError("Protocol request exceeds the maximum line size.")
        async with self._write_lock:
            self._writer.write(encoded)
            await self._writer.drain()
        try:
            response = await asyncio.wait_for(future, timeout)
        finally:
            self._pending.pop(request_id, None)
        if not response.ok:
            assert response.error is not None
            raise GameProtocolError(
                response.error.code, response.error.message, response.error.details
            )
        return response.result

    async def observe(self, grid_radius: int = 10) -> Observation:
        result = await self.request("observe", {"grid_radius": grid_radius}, timeout=60)
        return Observation.model_validate(result)

    async def execute_plan(self, plan: ActionPlan) -> str:
        result = await self.request(
            "execute_plan", {"plan": plan.model_dump(mode="json", exclude_none=True)}
        )
        return str(result["execution_id"])

    async def get_execution(self, execution_id: str) -> ExecutionResult:
        result = await self.request(
            "get_execution",
            {"execution_id": execution_id},
            timeout=60,
        )
        return ExecutionResult.model_validate(result)

    async def cancel_execution(self, execution_id: str) -> None:
        await self.request("cancel_execution", {"execution_id": execution_id})

    async def events(self) -> AsyncIterator[EventEnvelope]:
        while True:
            yield await self._events.get()

    async def wait_for_execution(self, execution_id: str, timeout: float = 130) -> ExecutionResult:
        terminal = {
            "COMPLETED",
            "COMPLETED_WITH_FAILURES",
            "CANCELLED",
            "TIMED_OUT",
            "BUDGET_EXCEEDED",
            "VALIDATION_FAILED",
            "GAME_INTERRUPTED",
            "FATAL_ERROR",
        }
        deadline = asyncio.get_running_loop().time() + timeout
        while True:
            result = await self.get_execution(execution_id)
            if result.status in terminal:
                return result
            remaining = deadline - asyncio.get_running_loop().time()
            if remaining <= 0:
                await self.cancel_execution(execution_id)
                raise TimeoutError(
                    f"Execution {execution_id} did not finish within {timeout} seconds."
                )
            await asyncio.sleep(min(0.25, remaining))

    async def _read_loop(self) -> None:
        assert self._reader is not None
        try:
            while True:
                line = await self._reader.readline()
                if not line:
                    raise GameProtocolError("DISCONNECTED", "The mod closed the connection.")
                if len(line) > MAX_LINE_BYTES:
                    raise GameProtocolError(
                        "MESSAGE_TOO_LARGE", "The mod sent an oversized message."
                    )
                raw = json.loads(line)
                if raw.get("type") == "response":
                    response = ResponseEnvelope.model_validate(raw)
                    future = self._pending.get(response.request_id)
                    if future is not None and not future.done():
                        future.set_result(response)
                elif raw.get("type") == "event":
                    event = EventEnvelope.model_validate(raw)
                    self._events.put_nowait(event)
                else:
                    raise GameProtocolError(
                        "INVALID_ENVELOPE", "The mod sent an unknown envelope type."
                    )
        except asyncio.CancelledError:
            raise
        except Exception as error:
            for future in self._pending.values():
                if not future.done():
                    future.set_exception(error)

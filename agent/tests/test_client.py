from __future__ import annotations

import asyncio
import json

import pytest

from stardew_agent.client import GameClient, GameProtocolError


@pytest.mark.asyncio
async def test_client_correlates_response() -> None:
    async def handle(reader: asyncio.StreamReader, writer: asyncio.StreamWriter) -> None:
        for _ in range(2):
            request = json.loads(await reader.readline())
            response = {
                "protocol_version": "1.0",
                "type": "response",
                "request_id": request["request_id"],
                "ok": True,
                "result": {"method": request["method"]},
            }
            writer.write(json.dumps(response).encode() + b"\n")
            await writer.drain()
        writer.close()
        await writer.wait_closed()

    server = await asyncio.start_server(handle, "127.0.0.1", 0)
    port = server.sockets[0].getsockname()[1]
    client = GameClient("127.0.0.1", port, "token")
    try:
        hello = await client.connect()
        assert hello == {"method": "hello"}
        pong = await client.request("ping", {})
        assert pong == {"method": "ping"}
    finally:
        await client.close()
        server.close()
        await server.wait_closed()


def test_client_rejects_non_loopback() -> None:
    with pytest.raises(ValueError):
        GameClient("0.0.0.0", 1234, "token")


@pytest.mark.asyncio
async def test_request_requires_connection() -> None:
    client = GameClient("127.0.0.1", 1234, "token")
    with pytest.raises(GameProtocolError) as error:
        await client.request("ping", {})
    assert error.value.code == "NOT_CONNECTED"

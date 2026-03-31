from __future__ import annotations

import json
import sqlite3
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from fastapi import FastAPI, WebSocket, WebSocketDisconnect
from fastapi.middleware.cors import CORSMiddleware
from pydantic import BaseModel, Field

DB_PATH = Path(__file__).with_name("mml_bridge.db")
_snapshot_ws_clients: set[WebSocket] = set()

app = FastAPI(title="MML Bridge Server", version="0.5.0")
app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)


def now_iso() -> str:
    return datetime.now(timezone.utc).isoformat()


def get_conn() -> sqlite3.Connection:
    conn = sqlite3.connect(DB_PATH)
    conn.row_factory = sqlite3.Row
    return conn


def init_db() -> None:
    with get_conn() as conn:
        conn.execute(
            """
            CREATE TABLE IF NOT EXISTS behavior_log (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                player_id TEXT NOT NULL,
                session_id TEXT NOT NULL,
                timestamp TEXT NOT NULL,
                event_type TEXT NOT NULL,
                event_data TEXT NOT NULL
            )
            """
        )
        conn.execute(
            """
            CREATE TABLE IF NOT EXISTS sync_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                asset_name TEXT NOT NULL,
                file_path TEXT NOT NULL,
                source_blend TEXT NOT NULL,
                exported_at TEXT NOT NULL,
                metadata_json TEXT NOT NULL,
                created_at TEXT NOT NULL
            )
            """
        )
        conn.execute(
            """
            CREATE TABLE IF NOT EXISTS sync_connection_state (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                connected INTEGER NOT NULL,
                source TEXT NOT NULL,
                source_blend TEXT NOT NULL,
                note TEXT NOT NULL,
                updated_at TEXT NOT NULL
            )
            """
        )
        conn.execute(
            """
            CREATE TABLE IF NOT EXISTS sync_snapshot_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                asset_name TEXT NOT NULL,
                source_blend TEXT NOT NULL,
                exported_at TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                metadata_json TEXT NOT NULL,
                created_at TEXT NOT NULL
            )
            """
        )
        existing = conn.execute(
            "SELECT id FROM sync_connection_state WHERE id = 1 LIMIT 1"
        ).fetchone()
        if existing is None:
            conn.execute(
                """
                INSERT INTO sync_connection_state (id, connected, source, source_blend, note, updated_at)
                VALUES (1, 0, '', '', '', ?)
                """,
                (now_iso(),),
            )


class CollectPayload(BaseModel):
    player_id: str = Field(min_length=1)
    session_id: str = Field(min_length=1)
    event_type: str = Field(min_length=1)
    event_data: dict[str, Any] = Field(default_factory=dict)
    timestamp: str = Field(default_factory=now_iso)


class SyncPublishPayload(BaseModel):
    asset_name: str = Field(min_length=1)
    file_path: str = Field(min_length=1)
    source_blend: str = ""
    exported_at: str = Field(default_factory=now_iso)
    metadata: dict[str, Any] = Field(default_factory=dict)


class ConnectionPayload(BaseModel):
    source: str = "blender"
    source_blend: str = ""
    note: str = ""


class SnapshotPublishPayload(BaseModel):
    asset_name: str = Field(min_length=1)
    source_blend: str = ""
    exported_at: str = Field(default_factory=now_iso)
    snapshot: dict[str, Any] = Field(default_factory=dict)
    metadata: dict[str, Any] = Field(default_factory=dict)


def row_to_sync_event(row: sqlite3.Row) -> dict[str, Any]:
    return {
        "id": row["id"],
        "asset_name": row["asset_name"],
        "assetName": row["asset_name"],
        "file_path": row["file_path"],
        "filePath": row["file_path"],
        "source_blend": row["source_blend"],
        "sourceBlend": row["source_blend"],
        "exported_at": row["exported_at"],
        "exportedAt": row["exported_at"],
        "metadata_json": row["metadata_json"],
        "metadataJson": row["metadata_json"],
        "created_at": row["created_at"],
        "createdAt": row["created_at"],
    }


def row_to_snapshot_event(row: sqlite3.Row) -> dict[str, Any]:
    payload_json = row["payload_json"] or "{}"
    metadata_json = row["metadata_json"] or "{}"
    try:
        snapshot = json.loads(payload_json)
    except json.JSONDecodeError:
        snapshot = {}

    return {
        "id": row["id"],
        "eventId": row["id"],
        "asset_name": row["asset_name"],
        "assetName": row["asset_name"],
        "source_blend": row["source_blend"],
        "sourceBlend": row["source_blend"],
        "exported_at": row["exported_at"],
        "exportedAt": row["exported_at"],
        "payload_json": payload_json,
        "payloadJson": payload_json,
        "metadata_json": metadata_json,
        "metadataJson": metadata_json,
        "created_at": row["created_at"],
        "createdAt": row["created_at"],
        "snapshot": snapshot,
    }


async def broadcast_snapshot_event(event: dict[str, Any]) -> None:
    if not _snapshot_ws_clients:
        return

    payload = {
        "ok": True,
        "type": "snapshot_event",
        "eventId": event.get("id", 0),
        "assetName": event.get("assetName", ""),
        "createdAt": event.get("createdAt", ""),
        "latest_event": event,
        "latestEvent": event,
    }

    stale_clients: list[WebSocket] = []
    for ws in list(_snapshot_ws_clients):
        try:
            await ws.send_json(payload)
        except Exception:
            stale_clients.append(ws)

    for ws in stale_clients:
        _snapshot_ws_clients.discard(ws)


def get_connection_state() -> dict[str, Any]:
    with get_conn() as conn:
        row = conn.execute(
            """
            SELECT connected, source, source_blend, note, updated_at
            FROM sync_connection_state
            WHERE id = 1
            LIMIT 1
            """
        ).fetchone()

    if row is None:
        return {
            "connected": False,
            "source": "",
            "source_blend": "",
            "note": "",
            "updated_at": now_iso(),
        }

    return {
        "connected": bool(row["connected"]),
        "source": row["source"],
        "source_blend": row["source_blend"],
        "note": row["note"],
        "updated_at": row["updated_at"],
    }


@app.on_event("startup")
def on_startup() -> None:
    init_db()


@app.get("/health")
def health() -> dict[str, str]:
    return {"status": "ok", "time": now_iso()}


@app.post("/collect")
def collect(payload: CollectPayload) -> dict[str, Any]:
    with get_conn() as conn:
        cursor = conn.execute(
            """
            INSERT INTO behavior_log (player_id, session_id, timestamp, event_type, event_data)
            VALUES (?, ?, ?, ?, ?)
            """,
            (
                payload.player_id,
                payload.session_id,
                payload.timestamp,
                payload.event_type,
                json.dumps(payload.event_data, ensure_ascii=False),
            ),
        )
        row_id = cursor.lastrowid

    return {"ok": True, "id": row_id}


@app.post("/sync/connection/connect")
def sync_connection_connect(payload: ConnectionPayload) -> dict[str, Any]:
    updated_at = now_iso()
    with get_conn() as conn:
        conn.execute(
            """
            UPDATE sync_connection_state
            SET connected = 1, source = ?, source_blend = ?, note = ?, updated_at = ?
            WHERE id = 1
            """,
            (payload.source, payload.source_blend, payload.note, updated_at),
        )
    return {"ok": True, "connected": True, "updated_at": updated_at}


@app.post("/sync/connection/disconnect")
def sync_connection_disconnect(payload: ConnectionPayload) -> dict[str, Any]:
    updated_at = now_iso()
    with get_conn() as conn:
        conn.execute(
            """
            UPDATE sync_connection_state
            SET connected = 0, source = ?, source_blend = ?, note = ?, updated_at = ?
            WHERE id = 1
            """,
            (payload.source, payload.source_blend, payload.note, updated_at),
        )
    return {"ok": True, "connected": False, "updated_at": updated_at}


@app.get("/sync/connection/status")
def sync_connection_status() -> dict[str, Any]:
    state = get_connection_state()
    return {"ok": True, **state}


@app.post("/sync/publish")
def sync_publish(payload: SyncPublishPayload) -> dict[str, Any]:
    state = get_connection_state()
    if not state["connected"]:
        return {
            "ok": False,
            "error": "connection is not active",
        }

    file_path = str(Path(payload.file_path).expanduser().resolve())
    if not Path(file_path).exists():
        return {
            "ok": False,
            "error": f"file not found: {file_path}",
        }
    if Path(file_path).suffix.lower() != ".fbx":
        return {
            "ok": False,
            "error": "only .fbx files are supported for live model sync",
        }

    created_at = now_iso()
    with get_conn() as conn:
        cursor = conn.execute(
            """
            INSERT INTO sync_events (
                asset_name, file_path, source_blend, exported_at, metadata_json, created_at
            )
            VALUES (?, ?, ?, ?, ?, ?)
            """,
            (
                payload.asset_name,
                file_path,
                payload.source_blend,
                payload.exported_at,
                json.dumps(payload.metadata, ensure_ascii=False),
                created_at,
            ),
        )
        event_id = cursor.lastrowid

    return {"ok": True, "id": event_id, "created_at": created_at}


@app.get("/sync/latest")
def sync_latest(since_id: int = 0, asset_name: str | None = None) -> dict[str, Any]:
    query = """
        SELECT id, asset_name, file_path, source_blend, exported_at, metadata_json, created_at
        FROM sync_events
        WHERE id > ?
    """
    params: list[Any] = [since_id]

    if asset_name:
        query += " AND asset_name = ?"
        params.append(asset_name)

    query += " ORDER BY id DESC LIMIT 1"

    with get_conn() as conn:
        row = conn.execute(query, params).fetchone()

    event = row_to_sync_event(row) if row else None
    return {"ok": True, "latest_event": event, "latestEvent": event}


@app.post("/sync/publish_snapshot")
async def sync_publish_snapshot(payload: SnapshotPublishPayload) -> dict[str, Any]:
    state = get_connection_state()
    if not state["connected"]:
        return {
            "ok": False,
            "error": "connection is not active",
        }

    snapshot = payload.snapshot if isinstance(payload.snapshot, dict) else {}
    objects = snapshot.get("objects", [])
    if not isinstance(objects, list):
        return {"ok": False, "error": "snapshot.objects must be a list"}

    created_at = now_iso()
    with get_conn() as conn:
        cursor = conn.execute(
            """
            INSERT INTO sync_snapshot_events (
                asset_name, source_blend, exported_at, payload_json, metadata_json, created_at
            )
            VALUES (?, ?, ?, ?, ?, ?)
            """,
            (
                payload.asset_name,
                payload.source_blend,
                payload.exported_at,
                json.dumps(snapshot, ensure_ascii=False),
                json.dumps(payload.metadata, ensure_ascii=False),
                created_at,
            ),
        )
        event_id = cursor.lastrowid

    metadata_json = json.dumps(payload.metadata, ensure_ascii=False)
    event = {
        "id": event_id,
        "eventId": event_id,
        "asset_name": payload.asset_name,
        "assetName": payload.asset_name,
        "source_blend": payload.source_blend,
        "sourceBlend": payload.source_blend,
        "exported_at": payload.exported_at,
        "exportedAt": payload.exported_at,
        "payload_json": json.dumps(snapshot, ensure_ascii=False),
        "payloadJson": json.dumps(snapshot, ensure_ascii=False),
        "metadata_json": metadata_json,
        "metadataJson": metadata_json,
        "created_at": created_at,
        "createdAt": created_at,
        "snapshot": snapshot,
    }
    await broadcast_snapshot_event(event)

    return {
        "ok": True,
        "id": event_id,
        "eventId": event_id,
        "object_count": len(objects),
        "objectCount": len(objects),
        "created_at": created_at,
        "createdAt": created_at,
    }


@app.get("/sync/latest_snapshot")
def sync_latest_snapshot(since_id: int = 0, asset_name: str | None = None) -> dict[str, Any]:
    query = """
        SELECT id, asset_name, source_blend, exported_at, payload_json, metadata_json, created_at
        FROM sync_snapshot_events
        WHERE id > ?
    """
    params: list[Any] = [since_id]

    if asset_name:
        query += " AND asset_name = ?"
        params.append(asset_name)

    query += " ORDER BY id DESC LIMIT 1"

    with get_conn() as conn:
        row = conn.execute(query, params).fetchone()

    event = row_to_snapshot_event(row) if row else None
    return {"ok": True, "latest_event": event, "latestEvent": event}


@app.websocket("/ws/sync_snapshot")
async def ws_sync_snapshot(websocket: WebSocket) -> None:
    await websocket.accept()
    _snapshot_ws_clients.add(websocket)
    await websocket.send_json(
        {
            "ok": True,
            "type": "connected",
            "channel": "sync_snapshot",
            "time": now_iso(),
        }
    )
    try:
        while True:
            incoming = await websocket.receive_json()
            if isinstance(incoming, dict) and incoming.get("type") == "ping":
                await websocket.send_json({"ok": True, "type": "pong", "time": now_iso()})
    except WebSocketDisconnect:
        pass
    finally:
        _snapshot_ws_clients.discard(websocket)


@app.websocket("/ws/chat")
async def ws_chat(websocket: WebSocket) -> None:
    await websocket.accept()
    await websocket.send_json({"type": "connected", "time": now_iso()})
    try:
        while True:
            incoming = await websocket.receive_json()
            message = str(incoming.get("message", "")).strip()
            await websocket.send_json(
                {
                    "type": "reply",
                    "message": f"[MML bridge] {message or 'empty message'}",
                    "time": now_iso(),
                }
            )
    except WebSocketDisconnect:
        return

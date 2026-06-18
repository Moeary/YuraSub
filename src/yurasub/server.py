from __future__ import annotations

import json
import re
from urllib.parse import parse_qs, urlsplit
from typing import Any

from .qt_bootstrap import prepare_qt_runtime

prepare_qt_runtime()

from PySide6.QtCore import QObject, Signal
from PySide6.QtNetwork import QHostAddress
from PySide6.QtNetwork import QTcpServer, QTcpSocket
from PySide6.QtWebSockets import QWebSocket, QWebSocketServer

from .payload import clean_text


class SubtitleDispatcher(QObject):
    subtitle_received = Signal(dict)
    style_received = Signal(dict)
    command_received = Signal(dict)
    clear_requested = Signal()
    client_count_changed = Signal(int)
    log_message = Signal(str)

    def _dispatch_payload(self, payload: dict[str, Any]) -> dict[str, Any] | None:
        message_type = str(payload.get("type") or "subtitle").lower()

        if message_type == "ping":
            return {"type": "pong"}

        if message_type in {"hello", "style"}:
            self._emit_style_and_commands(payload)
            return {"type": "ack", "for": message_type}

        if message_type in {"clear", "empty"}:
            self.clear_requested.emit()
            self.log_message.emit("Clear requested")
            return {"type": "ack", "for": message_type}

        if message_type in {"command", "config"}:
            self._emit_style_and_commands(payload)
            return {"type": "ack", "for": message_type}

        if _looks_like_noise_subtitle(payload):
            self.clear_requested.emit()
            self.log_message.emit(f"Ignored noise subtitle: {clean_text(payload.get('text'))}")
            return {"type": "ack", "ignored": True}

        self._emit_style_and_commands(payload)
        self.subtitle_received.emit(payload)
        text_preview = str(payload.get("text") or payload.get("subtitle") or payload.get("lyric") or "").strip()
        source = str(payload.get("source") or "unknown")
        self.log_message.emit(f"Subtitle received: {text_preview[:80]} [{source}]")
        return {"type": "ack", "for": message_type}

    def _emit_style_and_commands(self, payload: dict[str, Any]) -> None:
        style = payload.get("style")
        if isinstance(style, dict):
            self.style_received.emit(style)

        command: dict[str, Any] = {}
        for key in ("clickThrough", "click_through", "interactive", "geometry"):
            if key in payload:
                command[key] = payload[key]
        if command:
            self.command_received.emit(command)


class SubtitleServer(SubtitleDispatcher):
    def __init__(self, host: str = "127.0.0.1", port: int = 8765, parent: QObject | None = None) -> None:
        super().__init__(parent)
        self.host = host
        self.port = port
        self.error_string = ""
        self._clients: list[QWebSocket] = []
        self._server = QWebSocketServer("YuraSub", QWebSocketServer.SslMode.NonSecureMode, self)
        self._server.newConnection.connect(self._on_new_connection)

    @property
    def url(self) -> str:
        return f"ws://{self.host}:{self.port}"

    def start(self) -> bool:
        address = QHostAddress(self.host)
        if not self._server.listen(address, self.port):
            self.error_string = self._server.errorString()
            self.log_message.emit(f"WebSocket listen failed: {self.error_string}")
            return False
        self.log_message.emit(f"WebSocket listening on {self.url}")
        return True

    def stop(self) -> None:
        for socket in list(self._clients):
            socket.close()
            socket.deleteLater()
        self._clients.clear()
        self._server.close()
        self.client_count_changed.emit(0)

    def broadcast_media_command(self, command: str) -> None:
        payload = {"type": "mediaCommand", "command": command}
        for socket in list(self._clients):
            self._send_json(socket, payload)
        self.log_message.emit(f"Media command sent: {command}")

    def broadcast_media_seek(self, seconds: float) -> None:
        payload = {"type": "mediaCommand", "command": "seekTo", "time": max(0.0, float(seconds))}
        for socket in list(self._clients):
            self._send_json(socket, payload)
        self.log_message.emit(f"Media seek sent: {seconds:.2f}s")

    def _on_new_connection(self) -> None:
        socket = self._server.nextPendingConnection()
        if socket is None:
            return
        self._clients.append(socket)
        socket.textMessageReceived.connect(lambda message, s=socket: self._handle_text_message(s, message))
        socket.disconnected.connect(lambda s=socket: self._remove_client(s))
        self.client_count_changed.emit(len(self._clients))
        self.log_message.emit(f"Client connected: {len(self._clients)}")
        self._send_json(socket, {"type": "hello", "app": "YuraSub", "ok": True})

    def _remove_client(self, socket: QWebSocket) -> None:
        if socket in self._clients:
            self._clients.remove(socket)
        socket.deleteLater()
        self.client_count_changed.emit(len(self._clients))
        self.log_message.emit(f"Client disconnected: {len(self._clients)}")

    def _handle_text_message(self, socket: QWebSocket, message: str) -> None:
        if len(message) > 65536:
            self._send_json(socket, {"type": "error", "error": "message_too_large"})
            return

        payload = self._parse_message(message)
        response = self._dispatch_payload(payload)
        if response:
            self._send_json(socket, response)

    def _parse_message(self, message: str) -> dict[str, Any]:
        stripped = message.strip()
        if not stripped:
            return {"type": "clear"}
        if stripped.startswith("{"):
            try:
                parsed = json.loads(stripped)
                if isinstance(parsed, dict):
                    return parsed
            except json.JSONDecodeError as exc:
                self.log_message.emit(f"Invalid JSON payload: {exc}")
        return {"type": "subtitle", "text": message}

    def _send_json(self, socket: QWebSocket, payload: dict[str, Any]) -> None:
        socket.sendTextMessage(json.dumps(payload, ensure_ascii=False, separators=(",", ":")))


class SubtitleHttpServer(SubtitleDispatcher):
    def __init__(self, host: str = "127.0.0.1", port: int = 8766, parent: QObject | None = None) -> None:
        super().__init__(parent)
        self.host = host
        self.port = port
        self.error_string = ""
        self._server = QTcpServer(self)
        self._server.newConnection.connect(self._on_new_connection)
        self._buffers: dict[QTcpSocket, bytes] = {}
        self._commands: list[dict[str, Any]] = []
        self._next_command_id = 1

    @property
    def url(self) -> str:
        return f"http://{self.host}:{self.port}"

    def start(self) -> bool:
        address = QHostAddress(self.host)
        if not self._server.listen(address, self.port):
            self.error_string = self._server.errorString()
            self.log_message.emit(f"HTTP fallback listen failed: {self.error_string}")
            return False
        self.log_message.emit(f"HTTP fallback listening on {self.url}")
        return True

    def stop(self) -> None:
        for socket in list(self._buffers):
            socket.disconnectFromHost()
            socket.deleteLater()
        self._buffers.clear()
        self._server.close()

    def broadcast_media_command(self, command: str) -> None:
        self._queue_command({"type": "mediaCommand", "command": command})
        self.log_message.emit(f"HTTP media command queued: {command}")

    def broadcast_media_seek(self, seconds: float) -> None:
        payload = {"type": "mediaCommand", "command": "seekTo", "time": max(0.0, float(seconds))}
        self._queue_command(payload)
        self.log_message.emit(f"HTTP media seek queued: {seconds:.2f}s")

    def _queue_command(self, payload: dict[str, Any]) -> None:
        command = dict(payload)
        command["id"] = self._next_command_id
        self._next_command_id += 1
        self._commands.append(command)
        self._commands = self._commands[-200:]

    def _on_new_connection(self) -> None:
        while self._server.hasPendingConnections():
            socket = self._server.nextPendingConnection()
            if socket is None:
                continue
            self._buffers[socket] = b""
            socket.readyRead.connect(lambda s=socket: self._read_socket(s))
            socket.disconnected.connect(lambda s=socket: self._cleanup_socket(s))

    def _cleanup_socket(self, socket: QTcpSocket) -> None:
        self._buffers.pop(socket, None)
        socket.deleteLater()

    def _read_socket(self, socket: QTcpSocket) -> None:
        self._buffers[socket] = self._buffers.get(socket, b"") + bytes(socket.readAll())
        request = self._parse_http_request(self._buffers[socket])
        if request is None:
            return
        self._buffers.pop(socket, None)
        method, path, query, headers, body = request
        status = 200
        response: dict[str, Any] = {"ok": True}
        try:
            if method == "OPTIONS":
                response = {"ok": True}
            elif method == "GET" and path in {"/", "/health", "/status"}:
                response = {"ok": True, "app": "YuraSub", "commands": len(self._commands)}
            elif method == "GET" and path == "/commands":
                since = _safe_int(parse_qs(query).get("since", ["0"])[0], 0)
                response = {"ok": True, "commands": [command for command in self._commands if int(command["id"]) > since]}
            elif method == "POST" and path in {"/subtitle", "/push", "/"}:
                payload = json.loads(body.decode("utf-8-sig") or "{}")
                if not isinstance(payload, dict):
                    raise ValueError("JSON payload must be an object")
                dispatched = self._dispatch_payload(payload)
                response = dispatched or {"ok": True}
            else:
                status = 404
                response = {"ok": False, "error": "not_found"}
        except Exception as exc:  # noqa: BLE001 - keep fallback server resilient.
            status = 400
            response = {"ok": False, "error": str(exc)}
            self.log_message.emit(f"HTTP fallback request failed: {exc}")

        self._write_json(socket, status, response)

    def _parse_http_request(
        self, data: bytes
    ) -> tuple[str, str, str, dict[str, str], bytes] | None:
        header_end = data.find(b"\r\n\r\n")
        if header_end < 0:
            return None
        header_blob = data[:header_end].decode("iso-8859-1", errors="replace")
        lines = header_blob.split("\r\n")
        if not lines:
            return None
        method, target, _version = (lines[0].split(" ", 2) + ["", ""])[:3]
        headers: dict[str, str] = {}
        for line in lines[1:]:
            if ":" not in line:
                continue
            key, value = line.split(":", 1)
            headers[key.strip().lower()] = value.strip()
        content_length = _safe_int(headers.get("content-length"), 0)
        body_start = header_end + 4
        if len(data) < body_start + content_length:
            return None
        body = data[body_start : body_start + content_length]
        parsed = urlsplit(target)
        return method.upper(), parsed.path or "/", parsed.query, headers, body

    def _write_json(self, socket: QTcpSocket, status: int, payload: dict[str, Any]) -> None:
        body = json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        reason = "OK" if status < 400 else "Error"
        headers = [
            f"HTTP/1.1 {status} {reason}",
            "Content-Type: application/json; charset=utf-8",
            f"Content-Length: {len(body)}",
            "Access-Control-Allow-Origin: *",
            "Access-Control-Allow-Methods: GET, POST, OPTIONS",
            "Access-Control-Allow-Headers: Content-Type",
            "Connection: close",
            "",
            "",
        ]
        socket.write("\r\n".join(headers).encode("ascii") + body)
        socket.disconnectFromHost()


def _safe_int(value: Any, default: int) -> int:
    try:
        return int(value)
    except (TypeError, ValueError):
        return default


def _looks_like_noise_subtitle(payload: dict[str, Any]) -> bool:
    text = clean_text(payload.get("text") or payload.get("subtitle") or payload.get("lyric") or "")
    translation = clean_text(payload.get("translation") or payload.get("translated") or "")
    if translation or not text:
        return False
    if text in {"OK", "NO", "YES"}:
        return False
    if re.fullmatch(r"[A-Z0-9_-]{3,8}", text) and not re.search(r"[\u3040-\u30ff\u3400-\u9fff]", text):
        return True
    return False

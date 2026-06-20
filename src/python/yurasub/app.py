from __future__ import annotations

import argparse
import logging
import sys
from pathlib import Path

from .config import DEFAULT_CONFIG, load_config, resolve_config_path, save_config
from .qt_bootstrap import prepare_qt_runtime

logger = logging.getLogger(__name__)

prepare_qt_runtime()

from PySide6.QtCore import QCoreApplication, Qt
from PySide6.QtGui import QAction, QColor, QIcon, QPainter, QPixmap
from PySide6.QtWidgets import QApplication, QMenu, QSystemTrayIcon

from . import __version__
from .overlay import SubtitleOverlayWindow
from .server import SubtitleHttpServer, SubtitleServer


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(prog="YuraSub")
    parser.add_argument("--config", default=None, help="Path to JSON config file.")
    parser.add_argument("--host", default=None, help="WebSocket bind host.")
    parser.add_argument("--port", type=int, default=None, help="WebSocket bind port.")
    parser.add_argument("--http-port", type=int, default=None, help="HTTP fallback bind port.")
    parser.add_argument("--no-http", action="store_true", help="Disable HTTP fallback server.")
    parser.add_argument("--click-through", action="store_true", help="Start in click-through mode.")
    parser.add_argument("--debug", action="store_true", help="Print server events to stdout.")
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)

    # --- Load portable config ---------------------------------------------------
    config_path = resolve_config_path(args.config)
    config = load_config(config_path)

    # CLI args override saved config (only when explicitly provided).
    server_cfg = config.setdefault("server", {})
    if args.host is not None:
        server_cfg["host"] = args.host
    if args.port is not None:
        server_cfg["websocketPort"] = args.port
    if args.http_port is not None:
        server_cfg["httpPort"] = args.http_port

    host = server_cfg.get("host", "127.0.0.1")
    ws_port = int(server_cfg.get("websocketPort", 8765))
    http_port = int(server_cfg.get("httpPort", 8766))

    # --- Qt setup ---------------------------------------------------------------
    QCoreApplication.setApplicationName("YuraSub")
    QCoreApplication.setApplicationVersion(__version__)
    app = QApplication(sys.argv[:1])
    app.setQuitOnLastWindowClosed(False)

    overlay = SubtitleOverlayWindow(config=config)
    overlay.show()

    server = SubtitleServer(host, ws_port)
    server.subtitle_received.connect(lambda payload: overlay.apply_payload(payload))
    server.style_received.connect(lambda style: overlay.apply_style(style))
    server.command_received.connect(lambda command: overlay.apply_command(command))
    server.clear_requested.connect(lambda: overlay.clear_subtitle())
    overlay.media_command_requested.connect(lambda command: server.broadcast_media_command(command))
    overlay.media_seek_requested.connect(lambda seconds: server.broadcast_media_seek(seconds))

    http_server = None
    if not args.no_http:
        http_server = SubtitleHttpServer(host, http_port)
        http_server.subtitle_received.connect(lambda payload: overlay.apply_payload(payload))
        http_server.style_received.connect(lambda style: overlay.apply_style(style))
        http_server.command_received.connect(lambda command: overlay.apply_command(command))
        http_server.clear_requested.connect(lambda: overlay.clear_subtitle())
        overlay.media_command_requested.connect(lambda command: http_server.broadcast_media_command(command))
        overlay.media_seek_requested.connect(lambda seconds: http_server.broadcast_media_seek(seconds))

    if args.debug:
        server.log_message.connect(lambda message: print(message, flush=True))
        if http_server:
            http_server.log_message.connect(lambda message: print(message, flush=True))

    tray = _create_tray(app, overlay, server, config_path)

    ws_started = server.start()
    http_started = http_server.start() if http_server else False
    if ws_started:
        secondary = server.url if not http_started else f"{server.url} | HTTP {http_server.url}"
        overlay.show_status("YuraSub", secondary, timeout_ms=3500)
    else:
        overlay.show_status("YuraSub WebSocket failed", server.error_string or "unknown error", timeout_ms=0)

    if args.click_through:
        overlay.set_click_through(True)

    tray.show()
    exit_code = app.exec()

    # --- Save state on exit -----------------------------------------------------
    try:
        save_config(config_path, overlay.save_state())
    except OSError as exc:
        logger.warning("Failed to save config to %s: %s", config_path, exc)

    server.stop()
    if http_server:
        http_server.stop()
    return exit_code


def _create_tray(
    app: QApplication,
    overlay: SubtitleOverlayWindow,
    server: SubtitleServer,
    config_path: Path,
) -> QSystemTrayIcon:
    tray = QSystemTrayIcon(_make_icon(), app)
    tray.setToolTip(f"YuraSub {server.url}")

    menu = QMenu()
    interactive_action = QAction("解锁拖动/显示控制", menu)
    interactive_action.setCheckable(True)
    interactive_action.setChecked(not overlay.click_through)

    def set_interactive(enabled: bool) -> None:
        if enabled:
            overlay.unlock_for_editing()
        else:
            overlay.set_locked(True, local=True)

    def sync_interactive(click_through: bool) -> None:
        interactive_action.blockSignals(True)
        interactive_action.setChecked(not click_through)
        interactive_action.blockSignals(False)

    interactive_action.toggled.connect(lambda enabled: set_interactive(enabled))
    overlay.click_through_changed.connect(lambda click_through: sync_interactive(click_through))

    clear_action = QAction("清空字幕", menu)
    clear_action.triggered.connect(lambda: overlay.clear_subtitle())

    lock_action = QAction("锁定字幕", menu)
    lock_action.triggered.connect(lambda: overlay.set_locked(True, local=True))

    show_action = QAction("显示窗口", menu)
    show_action.triggered.connect(lambda: (overlay.unlock_for_editing(), overlay.show(), overlay.raise_()))

    def _restore_defaults() -> None:
        overlay.reset_to_defaults()
        try:
            save_config(config_path, overlay.save_state())
        except OSError as exc:
            logger.warning("Failed to save default config: %s", exc)

    restore_action = QAction("恢复默认设置", menu)
    restore_action.triggered.connect(lambda: _restore_defaults())

    quit_action = QAction("退出", menu)
    quit_action.triggered.connect(lambda: app.quit())

    menu.addAction(interactive_action)
    menu.addAction(lock_action)
    menu.addAction(clear_action)
    menu.addAction(show_action)
    menu.addAction(restore_action)
    menu.addSeparator()
    menu.addAction(quit_action)
    tray.setContextMenu(menu)

    def update_tooltip(count: int) -> None:
        tray.setToolTip(f"YuraSub {server.url} | clients: {count}")

    server.client_count_changed.connect(lambda count: update_tooltip(count))
    tray.activated.connect(
        lambda reason: (
            overlay.unlock_for_editing(),
            overlay.show(),
            overlay.raise_(),
            overlay.activateWindow(),
        )
        if reason == QSystemTrayIcon.ActivationReason.Trigger
        else None
    )
    return tray


def _make_icon() -> QIcon:
    pixmap = QPixmap(64, 64)
    pixmap.fill(QColor(0, 0, 0, 0))
    painter = QPainter(pixmap)
    painter.setRenderHint(QPainter.RenderHint.Antialiasing)
    painter.setBrush(QColor("#111827"))
    painter.setPen(QColor("#7dd3fc"))
    painter.drawRoundedRect(6, 6, 52, 52, 12, 12)
    painter.setPen(QColor("#ffffff"))
    painter.drawText(pixmap.rect(), Qt.AlignmentFlag.AlignCenter, "Y")
    painter.end()
    return QIcon(pixmap)

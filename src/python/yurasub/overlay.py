from __future__ import annotations

import ctypes
import math
import re
import sys
from typing import Any

from .qt_bootstrap import prepare_qt_runtime

prepare_qt_runtime()

from PySide6.QtCore import QPoint, QPointF, QRect, QRectF, QSize, QTimer, Qt, Signal
from PySide6.QtGui import QColor, QFont, QFontMetrics, QPainter, QPainterPath, QPen
from PySide6.QtWidgets import (
    QApplication,
    QColorDialog,
    QFontComboBox,
    QGridLayout,
    QHBoxLayout,
    QLabel,
    QPushButton,
    QSlider,
    QSpinBox,
    QToolButton,
    QVBoxLayout,
    QWidget,
)

from .config import CONFIG_SCHEMA_VERSION, DEFAULT_CONFIG
from .payload import clean_text, pick_text, read_bool


DEFAULT_STYLE: dict[str, Any] = {
    "fontFamily": "Microsoft YaHei UI",
    "fontSize": 34,
    "translationFontSize": 24,
    "textColor": "#ffffff",
    "textOpacity": 100,
    "translationColor": "#bfefff",
    "translationOpacity": 100,
    "outlineColor": "#101522",
    "outlineWidth": 4,
    "outlineOpacity": 100,
    "shadowColor": "#000000a0",
    "shadowOpacity": 65,
    "shadowOffsetX": 2,
    "shadowOffsetY": 3,
    "backgroundColor": "#00000000",
    "backgroundOpacity": 0,
    "backgroundRadius": 12,
    "paddingX": 18,
    "paddingY": 12,
    "lineGap": 6,
    "align": "center",
    "maxLines": 4,
}


def _as_int(value: Any, default: int, minimum: int | None = None, maximum: int | None = None) -> int:
    try:
        number = int(value)
    except (TypeError, ValueError):
        number = default
    if minimum is not None:
        number = max(minimum, number)
    if maximum is not None:
        number = min(maximum, number)
    return number


def _as_float(value: Any, default: float) -> float:
    try:
        return float(value)
    except (TypeError, ValueError):
        return default


def _color(value: Any, default: str) -> QColor:
    if isinstance(value, QColor):
        return QColor(value)
    if isinstance(value, (list, tuple)) and len(value) in {3, 4}:
        channels = [_as_int(part, 255, 0, 255) for part in value]
        if len(channels) == 3:
            channels.append(255)
        return QColor(*channels)
    if isinstance(value, str):
        stripped = value.strip()
        rgba = re.fullmatch(r"rgba?\(([^)]+)\)", stripped, flags=re.IGNORECASE)
        if rgba:
            parts = [part.strip() for part in rgba.group(1).split(",")]
            if len(parts) in {3, 4}:
                red = _as_int(parts[0], 255, 0, 255)
                green = _as_int(parts[1], 255, 0, 255)
                blue = _as_int(parts[2], 255, 0, 255)
                alpha = 255
                if len(parts) == 4:
                    alpha_value = _as_float(parts[3], 1.0)
                    alpha = round(alpha_value * 255) if alpha_value <= 1 else _as_int(alpha_value, 255, 0, 255)
                return QColor(red, green, blue, max(0, min(255, alpha)))
        css_hex = stripped[1:] if stripped.startswith("#") else stripped
        if re.fullmatch(r"[0-9a-fA-F]{8}", css_hex):
            return QColor(
                int(css_hex[0:2], 16),
                int(css_hex[2:4], 16),
                int(css_hex[4:6], 16),
                int(css_hex[6:8], 16),
            )
    candidate = QColor(str(value or default))
    if not candidate.isValid():
        candidate = QColor(default)
    return candidate


def _with_opacity(color: QColor, value: Any, default: int = 100) -> QColor:
    opacity = _as_int(value, default, 0, 100)
    result = QColor(color)
    result.setAlpha(round(color.alpha() * opacity / 100))
    return result


def color_to_css(color: QColor) -> str:
    return f"#{color.red():02x}{color.green():02x}{color.blue():02x}{color.alpha():02x}"


def color_to_rgba(color: QColor) -> str:
    return f"rgba({color.red()}, {color.green()}, {color.blue()}, {color.alpha() / 255:.2f})"


def format_time(seconds: Any) -> str:
    value = max(0, int(_as_float(seconds, 0.0)))
    minutes, second = divmod(value, 60)
    hours, minute = divmod(minutes, 60)
    if hours:
        return f"{hours:d}:{minute:02d}:{second:02d}"
    return f"{minute:d}:{second:02d}"


def _global_pos(event: Any) -> QPoint:
    if hasattr(event, "globalPosition"):
        return event.globalPosition().toPoint()
    return event.globalPos()


class ColorToolButton(QPushButton):
    color_changed = Signal(QColor)

    def __init__(self, label: str, color: QColor, parent: QWidget | None = None) -> None:
        super().__init__(label, parent)
        self._color = QColor(color)
        self.clicked.connect(self._choose_color)
        self._sync_style()

    @property
    def color(self) -> QColor:
        return QColor(self._color)

    def set_color(self, color: QColor) -> None:
        if not color.isValid():
            return
        self._color = QColor(color)
        self._sync_style()

    def _choose_color(self) -> None:
        selected = QColorDialog.getColor(self._color, self, self.text(), QColorDialog.ColorDialogOption.ShowAlphaChannel)
        if selected.isValid():
            self.set_color(selected)
            self.color_changed.emit(QColor(self._color))

    def _sync_style(self) -> None:
        text_color = "#111827" if self._color.lightness() > 145 and self._color.alpha() > 80 else "#ffffff"
        self.setToolTip(color_to_rgba(self._color))
        self.setStyleSheet(
            "QPushButton {"
            f"background: rgba({self._color.red()}, {self._color.green()}, {self._color.blue()}, {max(self._color.alpha(), 72)});"
            f"color: {text_color};"
            "border: 1px solid rgba(255, 255, 255, 92);"
            "border-radius: 8px;"
            "padding: 4px 10px;"
            "}"
        )


class SeekSlider(QSlider):
    seek_released = Signal()

    def __init__(self, parent: QWidget | None = None) -> None:
        super().__init__(Qt.Orientation.Horizontal, parent)
        self.setRange(0, 1000)
        self.setPageStep(0)
        self.setSingleStep(1)

    def mousePressEvent(self, event: Any) -> None:
        if event.button() == Qt.MouseButton.LeftButton:
            self.setSliderDown(True)
            self._set_value_from_position(event.position().x())
            event.accept()
            return
        super().mousePressEvent(event)

    def mouseMoveEvent(self, event: Any) -> None:
        if self.isSliderDown():
            self._set_value_from_position(event.position().x())
            event.accept()
            return
        super().mouseMoveEvent(event)

    def mouseReleaseEvent(self, event: Any) -> None:
        if event.button() == Qt.MouseButton.LeftButton and self.isSliderDown():
            self._set_value_from_position(event.position().x())
            self.setSliderDown(False)
            self.seek_released.emit()
            event.accept()
            return
        super().mouseReleaseEvent(event)

    def _set_value_from_position(self, x: float) -> None:
        width = max(1, self.width())
        ratio = max(0.0, min(1.0, x / width))
        self.setValue(round(self.minimum() + ratio * (self.maximum() - self.minimum())))


def _tool_button(text: str, tooltip: str = "") -> QToolButton:
    button = QToolButton()
    button.setText(text)
    button.setToolTip(tooltip)
    button.setAutoRaise(True)
    button.setCursor(Qt.CursorShape.PointingHandCursor)
    button.setMinimumSize(34, 30)
    return button


def _spin(minimum: int, maximum: int, value: int) -> QSpinBox:
    spin = QSpinBox()
    spin.setRange(minimum, maximum)
    spin.setValue(value)
    spin.setMinimumWidth(70)
    return spin


class SubtitleCanvas(QWidget):
    def __init__(self, parent: QWidget | None = None) -> None:
        super().__init__(parent)
        self._text = ""
        self._translation = ""
        self._style = dict(DEFAULT_STYLE)
        self._local_override_keys: set[str] = set()
        self.setAttribute(Qt.WidgetAttribute.WA_TransparentForMouseEvents, True)

    @property
    def style(self) -> dict[str, Any]:
        return dict(self._style)

    def apply_style(self, style: dict[str, Any], *, local: bool = False) -> None:
        if local:
            self._local_override_keys.update(style)
            self._style.update(style)
        else:
            for key, value in style.items():
                if key not in self._local_override_keys:
                    self._style[key] = value
        self.update()

    def clear_local_overrides(self) -> None:
        self._local_override_keys.clear()

    def set_subtitle(self, text: str, translation: str = "") -> None:
        self._text = clean_text(text)
        self._translation = clean_text(translation)
        self.update()

    def clear(self) -> None:
        self.set_subtitle("", "")

    def paintEvent(self, _event: Any) -> None:
        if not self._text and not self._translation:
            return

        painter = QPainter(self)
        painter.setRenderHints(
            QPainter.RenderHint.Antialiasing
            | QPainter.RenderHint.TextAntialiasing
            | QPainter.RenderHint.SmoothPixmapTransform
        )

        style = self._style
        rect = QRectF(self.rect())
        padding_x = _as_int(style.get("paddingX"), DEFAULT_STYLE["paddingX"], 0, 120)
        padding_y = _as_int(style.get("paddingY"), DEFAULT_STYLE["paddingY"], 0, 80)

        background = _with_opacity(
            _color(style.get("backgroundColor"), DEFAULT_STYLE["backgroundColor"]),
            style.get("backgroundOpacity"),
            DEFAULT_STYLE["backgroundOpacity"],
        )
        if background.alpha() > 0:
            radius = _as_float(style.get("backgroundRadius"), DEFAULT_STYLE["backgroundRadius"])
            painter.setPen(Qt.PenStyle.NoPen)
            painter.setBrush(background)
            painter.drawRoundedRect(rect.adjusted(1, 1, -1, -1), radius, radius)

        draw_rect = rect.adjusted(padding_x, padding_y, -padding_x, -padding_y)
        if draw_rect.width() <= 20 or draw_rect.height() <= 20:
            return

        family = str(style.get("fontFamily") or DEFAULT_STYLE["fontFamily"])
        main_font = QFont(family, _as_int(style.get("fontSize"), DEFAULT_STYLE["fontSize"], 12, 96))
        main_font.setWeight(QFont.Weight.Bold)
        translation_font = QFont(family, _as_int(style.get("translationFontSize"), DEFAULT_STYLE["translationFontSize"], 10, 72))
        translation_font.setWeight(QFont.Weight.Medium)

        layout = self._build_layout(draw_rect, main_font, translation_font)
        if not layout:
            return

        total_height = sum(item["height"] for item in layout)
        total_height += max(0, len(layout) - 1) * _as_int(style.get("lineGap"), DEFAULT_STYLE["lineGap"], 0, 30)
        y = draw_rect.top() + max(0, (draw_rect.height() - total_height) / 2)
        line_gap = _as_int(style.get("lineGap"), DEFAULT_STYLE["lineGap"], 0, 30)

        for item in layout:
            font = item["font"]
            metrics = QFontMetrics(font)
            baseline = y + metrics.ascent()
            self._draw_text_line(
                painter=painter,
                text=item["text"],
                font=font,
                baseline=baseline,
                available=draw_rect,
                fill=item["color"],
            )
            y += item["height"] + line_gap

    def _build_layout(self, rect: QRectF, main_font: QFont, translation_font: QFont) -> list[dict[str, Any]]:
        style = self._style
        max_lines = _as_int(style.get("maxLines"), DEFAULT_STYLE["maxLines"], 1, 10)
        text_color = _with_opacity(
            _color(style.get("textColor"), DEFAULT_STYLE["textColor"]),
            style.get("textOpacity"),
            DEFAULT_STYLE["textOpacity"],
        )
        translation_color = _with_opacity(
            _color(style.get("translationColor"), DEFAULT_STYLE["translationColor"]),
            style.get("translationOpacity"),
            DEFAULT_STYLE["translationOpacity"],
        )
        width = int(rect.width())

        main_lines = _wrap_text(self._text, main_font, width, max_lines)
        remaining = max(0, max_lines - len(main_lines))
        translation_lines = _wrap_text(self._translation, translation_font, width, remaining) if remaining else []

        layout: list[dict[str, Any]] = []
        for line in main_lines:
            metrics = QFontMetrics(main_font)
            layout.append({"text": line, "font": main_font, "height": metrics.height(), "color": text_color})
        for line in translation_lines:
            metrics = QFontMetrics(translation_font)
            layout.append({"text": line, "font": translation_font, "height": metrics.height(), "color": translation_color})
        return layout

    def _draw_text_line(
        self,
        painter: QPainter,
        text: str,
        font: QFont,
        baseline: float,
        available: QRectF,
        fill: QColor,
    ) -> None:
        if not text:
            return

        style = self._style
        metrics = QFontMetrics(font)
        line_width = metrics.horizontalAdvance(text)
        align = str(style.get("align") or DEFAULT_STYLE["align"]).lower()
        if align == "left":
            x = available.left()
        elif align == "right":
            x = available.right() - line_width
        else:
            x = available.left() + (available.width() - line_width) / 2

        path = QPainterPath()
        path.addText(QPointF(x, baseline), font, text)

        shadow = _with_opacity(
            _color(style.get("shadowColor"), DEFAULT_STYLE["shadowColor"]),
            style.get("shadowOpacity"),
            DEFAULT_STYLE["shadowOpacity"],
        )
        if shadow.alpha() > 0:
            shadow_path = QPainterPath(path)
            shadow_path.translate(
                _as_float(style.get("shadowOffsetX"), DEFAULT_STYLE["shadowOffsetX"]),
                _as_float(style.get("shadowOffsetY"), DEFAULT_STYLE["shadowOffsetY"]),
            )
            painter.fillPath(shadow_path, shadow)

        outline_width = _as_float(style.get("outlineWidth"), DEFAULT_STYLE["outlineWidth"])
        if outline_width > 0:
            outline = _with_opacity(
                _color(style.get("outlineColor"), DEFAULT_STYLE["outlineColor"]),
                style.get("outlineOpacity"),
                DEFAULT_STYLE["outlineOpacity"],
            )
            pen = QPen(outline, outline_width)
            pen.setJoinStyle(Qt.PenJoinStyle.RoundJoin)
            painter.strokePath(path, pen)
        painter.fillPath(path, fill)


class SubtitleOverlayWindow(QWidget):
    click_through_changed = Signal(bool)
    lock_changed = Signal(bool)
    style_changed = Signal(dict)
    geometry_override_changed = Signal(bool)
    media_command_requested = Signal(str)
    media_seek_requested = Signal(float)

    def __init__(self, config: dict[str, Any] | None = None) -> None:
        super().__init__()
        self._config = config or {}
        self.canvas = SubtitleCanvas(self)
        self.controls = QWidget(self)
        self.toolbar = QWidget(self.controls)
        self.style_panel = QWidget(self.controls)
        self._click_through = False
        self._drag_mode: str | None = None
        self._drag_global = QPoint()
        self._drag_geometry = QRect()
        self._resize_margin = 26
        self._locked = False
        self._remote_interaction_enabled = False
        self._local_geometry_override = False
        self._status_active = False
        self._syncing_controls = False
        self._syncing_progress = False
        self._media_duration = 0.0
        self._media_position = 0.0
        self._media_paused = True
        self._control_color = QColor(DEFAULT_CONFIG["style"]["controlColor"])
        self._control_opacity = int(DEFAULT_CONFIG["style"]["controlOpacity"])
        self._status_timer = QTimer(self)
        self._status_timer.setSingleShot(True)
        self._status_timer.timeout.connect(self._clear_status)

        self.setWindowTitle("YuraSub")
        self.setWindowFlags(
            Qt.WindowType.FramelessWindowHint
            | Qt.WindowType.WindowStaysOnTopHint
            | Qt.WindowType.Tool
            | Qt.WindowType.NoDropShadowWindowHint
        )
        self.setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground, True)
        self.setMouseTracking(True)
        self._build_controls()
        self._restore_from_config()
        self._layout_children()

    def _restore_from_config(self) -> None:
        """Restore window geometry, style, and click-through from saved config."""
        cfg = self._config

        # Restore style first (before layout) so canvas knows the real style.
        style_cfg = cfg.get("style")
        if isinstance(style_cfg, dict):
            # Merge with DEFAULT_STYLE to get all keys, then apply as local
            # overrides so remote payloads won't clobber saved values.
            merged = dict(DEFAULT_STYLE)
            merged.update(style_cfg)
            self.canvas.apply_style(merged, local=True)

            # Restore control color (not part of canvas style).
            cc = style_cfg.get("controlColor", DEFAULT_CONFIG["style"]["controlColor"])
            co = style_cfg.get("controlOpacity", DEFAULT_CONFIG["style"]["controlOpacity"])
            self._control_color = _color(cc, DEFAULT_CONFIG["style"]["controlColor"])
            self._control_opacity = _as_int(co, int(DEFAULT_CONFIG["style"]["controlOpacity"]), 0, 100)
            self._control_color.setAlpha(round(self._control_opacity * 255 / 100))
            self._update_control_stylesheet()

        self._sync_controls_from_style()

        # Restore geometry.
        win = cfg.get("window")
        if isinstance(win, dict) and win.get("x") is not None and win.get("y") is not None:
            width = _as_int(win.get("width"), 1100, 280, 3840)
            height = _as_int(win.get("height"), 180, 80, 2160)
            self.resize(width, height)
            self.move(_as_int(win.get("x"), 200), _as_int(win.get("y"), 500))
            self._local_geometry_override = True
        else:
            self.resize(1100, 180)
            self._place_default()

        # Restore click-through / lock state.
        if isinstance(win, dict):
            ct = read_bool(win.get("clickThrough"))
            if ct:
                self.set_click_through(True)

    def save_state(self) -> dict[str, Any]:
        """Return the current window state as a config-compatible dict."""
        geo = self.geometry()
        style = dict(self.canvas._style)
        # Persist control color (not part of canvas rendering style).
        style["controlColor"] = color_to_css(self._control_color)
        style["controlOpacity"] = self._control_opacity
        return {
            "schemaVersion": CONFIG_SCHEMA_VERSION,
            "server": dict(self._config.get("server", DEFAULT_CONFIG["server"])),
            "window": {
                "x": geo.x(),
                "y": geo.y(),
                "width": geo.width(),
                "height": geo.height(),
                "clickThrough": self._click_through,
            },
            "style": style,
        }

    @property
    def click_through(self) -> bool:
        return self._click_through

    @property
    def locked(self) -> bool:
        return self._locked

    @property
    def style(self) -> dict[str, Any]:
        return self.canvas.style

    def _build_controls(self) -> None:
        self.controls.setObjectName("overlayControls")
        self.toolbar.setObjectName("overlayToolbar")
        self.style_panel.setObjectName("overlayStylePanel")
        self._update_control_stylesheet()

        root = QVBoxLayout(self.controls)
        root.setContentsMargins(0, 0, 0, 0)
        root.setSpacing(4)

        toolbar_layout = QHBoxLayout(self.toolbar)
        toolbar_layout.setContentsMargins(14, 4, 14, 4)
        toolbar_layout.setSpacing(18)
        toolbar_layout.addStretch(1)

        self.font_bigger_button = _tool_button("A+", "增大字号")
        self.font_smaller_button = _tool_button("A-", "减小字号")
        self.previous_button = _tool_button("◀▌", "上一首")
        self.play_pause_button = _tool_button("Ⅱ", "播放/暂停")
        self.next_button = _tool_button("▐▶", "下一首")
        self.progress_slider = SeekSlider()
        self.progress_slider.setMinimumWidth(260)
        self.progress_slider.setEnabled(False)
        self.progress_slider.setCursor(Qt.CursorShape.PointingHandCursor)
        self.progress_slider.setToolTip("拖动跳转播放位置")
        self.time_label = QLabel("0:00 / 0:00")
        self.time_label.setMinimumWidth(92)
        self.time_label.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self.palette_button = _tool_button("◐", "样式")
        self.clear_button = _tool_button("×", "清空字幕")
        self.lock_button = _tool_button("▣", "锁定并点击穿透")

        for button in (
            self.font_bigger_button,
            self.font_smaller_button,
            self.previous_button,
        ):
            toolbar_layout.addWidget(button)
        toolbar_layout.addWidget(self.progress_slider, 4)
        toolbar_layout.addWidget(self.time_label)
        for button in (
            self.next_button,
            self.play_pause_button,
            self.palette_button,
            self.clear_button,
            self.lock_button,
        ):
            toolbar_layout.addWidget(button)
        toolbar_layout.addStretch(1)
        root.addWidget(self.toolbar)

        panel_layout = QGridLayout(self.style_panel)
        panel_layout.setContentsMargins(14, 6, 14, 6)
        panel_layout.setHorizontalSpacing(12)
        panel_layout.setVerticalSpacing(0)
        self.font_combo = QFontComboBox()
        self.font_combo.setMinimumWidth(240)
        self.font_combo.setMaximumWidth(420)
        self.font_size_spin = _spin(12, 120, int(DEFAULT_STYLE["fontSize"]))
        self.text_color_button = ColorToolButton("正文颜色", _color(DEFAULT_STYLE["textColor"], "#ffffff"))
        self.outline_color_button = ColorToolButton("描边", _color(DEFAULT_STYLE["outlineColor"], "#101522"))
        self.control_color_button = ColorToolButton("图标颜色", _color(DEFAULT_CONFIG["style"]["controlColor"], "#f5fff8e6"))

        panel_layout.addWidget(QLabel("字体"), 0, 0)
        panel_layout.addWidget(self.font_combo, 0, 1)
        panel_layout.addWidget(QLabel("正文字号"), 0, 2)
        panel_layout.addWidget(self.font_size_spin, 0, 3)
        panel_layout.addWidget(self.text_color_button, 0, 4)
        panel_layout.addWidget(self.outline_color_button, 0, 5)
        panel_layout.addWidget(self.control_color_button, 0, 6)
        panel_layout.setColumnStretch(1, 1)

        root.addWidget(self.style_panel)
        self.style_panel.hide()

        self.font_bigger_button.clicked.connect(lambda: self._step_font_size(2))
        self.font_smaller_button.clicked.connect(lambda: self._step_font_size(-2))
        self.previous_button.clicked.connect(lambda: self.media_command_requested.emit("previousTrack"))
        self.play_pause_button.clicked.connect(lambda: self.media_command_requested.emit("playPause"))
        self.next_button.clicked.connect(lambda: self.media_command_requested.emit("nextTrack"))
        self.progress_slider.valueChanged.connect(lambda _value: self._update_progress_time_from_slider())
        self.progress_slider.seek_released.connect(self._seek_from_progress_slider)
        self.palette_button.clicked.connect(self.toggle_style_panel)
        self.clear_button.clicked.connect(self.clear_subtitle)
        self.lock_button.clicked.connect(lambda: self.set_locked(True, local=True))

        self.font_combo.currentFontChanged.connect(lambda font: self._apply_control_style({"fontFamily": font.family()}))
        self.font_size_spin.valueChanged.connect(lambda value: self._apply_control_style({"fontSize": value}))
        self.text_color_button.color_changed.connect(lambda color: self._apply_control_style({"textColor": color_to_css(color)}))
        self.outline_color_button.color_changed.connect(lambda color: self._apply_control_style({"outlineColor": color_to_css(color)}))
        self.control_color_button.color_changed.connect(self._apply_control_color)

    def toggle_style_panel(self) -> None:
        self.style_panel.setVisible(self.style_panel.isHidden())
        self._layout_children()

    def _step_font_size(self, delta: int) -> None:
        style = self.style
        self.apply_local_style(
            {
                "fontSize": max(12, min(120, _as_int(style.get("fontSize"), DEFAULT_STYLE["fontSize"]) + delta)),
                "translationFontSize": max(
                    10,
                    min(96, _as_int(style.get("translationFontSize"), DEFAULT_STYLE["translationFontSize"]) + delta),
                ),
            }
        )

    def _apply_control_style(self, style: dict[str, Any]) -> None:
        if self._syncing_controls:
            return
        self.apply_local_style(style)

    def _apply_control_color(self, color: QColor) -> None:
        """Apply a new control/icon color and persist it."""
        if self._syncing_controls:
            return
        self._control_color = QColor(color)
        self._control_opacity = round(color.alpha() * 100 / 255)
        self._update_control_stylesheet()
        self.update()  # repaint grip
        self.style_changed.emit(self.style)

    def _update_control_stylesheet(self) -> None:
        """Rebuild the controls stylesheet using the current control color."""
        c = self._control_color
        alpha = max(c.alpha(), 72)
        css = f"""
            QWidget#overlayToolbar {{
                background: rgba(12, 18, 20, 0);
                border: 1px solid rgba(229, 241, 232, 18);
                border-radius: 12px;
            }}
            QWidget#overlayStylePanel {{
                background: rgba(12, 18, 20, 30);
                border: 1px solid rgba(229, 241, 232, 34);
                border-radius: 12px;
            }}
            QToolButton {{
                color: rgba({c.red()}, {c.green()}, {c.blue()}, {alpha});
                background: transparent;
                font-size: 22px;
                font-weight: 700;
                border: none;
                padding: 3px 8px;
            }}
            QToolButton:hover {{
                background: rgba({c.red()}, {c.green()}, {c.blue()}, 34);
                border-radius: 8px;
            }}
            QLabel {{
                color: rgba({c.red()}, {c.green()}, {c.blue()}, {max(c.alpha() - 6, 0)});
                font-size: 12px;
            }}
            QSpinBox, QFontComboBox {{
                background: rgba(245, 255, 248, 168);
                color: #111827;
                border: 1px solid rgba(255, 255, 255, 74);
                border-radius: 6px;
                padding: 2px 6px;
            }}
            QSlider::groove:horizontal {{
                height: 4px;
                border-radius: 2px;
                background: rgba(245, 255, 248, 54);
            }}
            QSlider::sub-page:horizontal {{
                height: 4px;
                border-radius: 2px;
                background: rgba({c.red()}, {c.green()}, {c.blue()}, {max(c.alpha() - 40, 60)});
            }}
            QSlider::handle:horizontal {{
                width: 12px;
                margin: -5px 0;
                border-radius: 6px;
                background: rgba({c.red()}, {c.green()}, {c.blue()}, {max(c.alpha() - 6, 0)});
            }}
        """
        self.controls.setStyleSheet(css)

    def _sync_controls_from_style(self) -> None:
        if not hasattr(self, "font_combo"):
            return
        self._syncing_controls = True
        style = self.style
        self.font_combo.setCurrentFont(QFont(str(style.get("fontFamily", DEFAULT_STYLE["fontFamily"]))))
        self.font_size_spin.setValue(_as_int(style.get("fontSize"), DEFAULT_STYLE["fontSize"], 12, 120))
        self.text_color_button.set_color(_color(style.get("textColor"), DEFAULT_STYLE["textColor"]))
        self.outline_color_button.set_color(_color(style.get("outlineColor"), DEFAULT_STYLE["outlineColor"]))
        # Control color comes from the overlay field, not from canvas style.
        self.control_color_button.set_color(self._control_color)
        self._syncing_controls = False

    def apply_payload(self, payload: dict[str, Any]) -> None:
        style = payload.get("style")
        if isinstance(style, dict):
            self.apply_style(style)
        self.apply_command(payload)
        self.apply_media(payload.get("media"))

        text, translation = pick_text(payload)
        self._status_active = False
        self._status_timer.stop()
        if text or translation:
            self.canvas.set_subtitle(text, translation)
        else:
            self.clear_subtitle()

    def apply_media(self, media: Any) -> None:
        if not isinstance(media, dict):
            return
        duration = _as_float(media.get("duration"), 0.0)
        position = _as_float(media.get("currentTime"), 0.0)
        if not math.isfinite(duration) or duration <= 0:
            duration = 0.0
        if not math.isfinite(position):
            position = 0.0
        position = max(0.0, min(position, duration)) if duration > 0 else max(0.0, position)
        self._media_duration = duration
        self._media_position = position
        self._media_paused = bool(media.get("paused", True))
        self._sync_progress_controls()

    def apply_style(self, style: dict[str, Any]) -> None:
        self.canvas.apply_style(style, local=False)
        self.apply_command(style)
        self._sync_controls_from_style()
        self.style_changed.emit(self.style)

    def apply_local_style(self, style: dict[str, Any]) -> None:
        self.canvas.apply_style(style, local=True)
        self._sync_controls_from_style()
        self.style_changed.emit(self.style)

    def clear_local_style_overrides(self) -> None:
        self.canvas.clear_local_overrides()
        self.style_changed.emit(self.style)

    def reset_to_defaults(self) -> None:
        """Reset all style, control color, geometry, and server config to built-in defaults."""
        # Reset stored config so save_state() writes default ports.
        self._config = {}

        # Reset canvas style to defaults and mark all as local overrides.
        self.canvas._style = dict(DEFAULT_STYLE)
        self.canvas._local_override_keys = set(DEFAULT_STYLE.keys())
        self.canvas.update()

        # Reset control color.
        self._control_color = QColor(DEFAULT_CONFIG["style"]["controlColor"])
        self._control_opacity = int(DEFAULT_CONFIG["style"]["controlOpacity"])
        self._update_control_stylesheet()

        # Reset geometry.
        self._local_geometry_override = False
        self.resize(1100, 180)
        self._place_default()

        # Reset lock / click-through.
        self.unlock_for_editing()

        self._sync_controls_from_style()
        self.style_changed.emit(self.style)

    def apply_command(self, command: dict[str, Any]) -> None:
        if self._remote_interaction_enabled and "interactive" in command:
            interactive = read_bool(command.get("interactive"))
            if interactive is not None:
                self.set_click_through(not interactive, local=False)

        click_through = command.get("clickThrough", command.get("click_through"))
        parsed = read_bool(click_through)
        if self._remote_interaction_enabled and parsed is not None:
            self.set_click_through(parsed, local=False)

        geometry = command.get("geometry")
        if isinstance(geometry, dict) and not self._local_geometry_override:
            self.apply_geometry(geometry)

    def apply_geometry(self, geometry: dict[str, Any]) -> None:
        screen = QApplication.primaryScreen()
        available = screen.availableGeometry() if screen else QRect(0, 0, 1280, 720)
        width = _as_int(geometry.get("width"), self.width(), 280, available.width())
        height = _as_int(geometry.get("height"), self.height(), 80, available.height())
        self.resize(width, height)

        if "x" in geometry and "y" in geometry:
            self.move(_as_int(geometry.get("x"), self.x()), _as_int(geometry.get("y"), self.y()))
            return

        anchor = str(geometry.get("anchor") or "bottom").lower()
        margin_bottom = _as_int(geometry.get("marginBottom"), 80, 0, available.height())
        margin_top = _as_int(geometry.get("marginTop"), 80, 0, available.height())
        x = available.left() + (available.width() - width) // 2
        if anchor == "top":
            y = available.top() + margin_top
        elif anchor == "center":
            y = available.top() + (available.height() - height) // 2
        else:
            y = available.bottom() - height - margin_bottom
        self.move(x, y)

    def show_status(self, text: str, secondary: str = "", timeout_ms: int = 4000) -> None:
        self._status_active = True
        self.canvas.set_subtitle(text, secondary)
        if timeout_ms > 0:
            self._status_timer.start(timeout_ms)

    def clear_subtitle(self) -> None:
        self._status_active = False
        self._status_timer.stop()
        self.canvas.clear()

    def _sync_progress_controls(self) -> None:
        if not hasattr(self, "progress_slider"):
            return
        enabled = self._media_duration > 0
        self.progress_slider.setEnabled(enabled)
        if self.progress_slider.isSliderDown():
            self._update_progress_time_from_slider()
            return

        self._syncing_progress = True
        value = round((self._media_position / self._media_duration) * 1000) if enabled else 0
        self.progress_slider.setValue(max(0, min(1000, value)))
        self._syncing_progress = False
        self.time_label.setText(f"{format_time(self._media_position)} / {format_time(self._media_duration)}")
        self.play_pause_button.setText("▶" if self._media_paused else "Ⅱ")

    def _update_progress_time_from_slider(self) -> None:
        if self._syncing_progress:
            return
        if self._media_duration <= 0:
            self.time_label.setText("0:00 / 0:00")
            return
        position = self._media_duration * self.progress_slider.value() / 1000
        self.time_label.setText(f"{format_time(position)} / {format_time(self._media_duration)}")

    def _seek_from_progress_slider(self) -> None:
        if self._media_duration <= 0:
            return
        position = self._media_duration * self.progress_slider.value() / 1000
        self._media_position = position
        self._update_progress_time_from_slider()
        self.media_seek_requested.emit(position)

    def set_click_through(self, enabled: bool, *, local: bool = True) -> None:
        if local:
            self._remote_interaction_enabled = False
        enabled = bool(enabled)
        if self._click_through == enabled:
            return
        self._click_through = enabled
        self.setAttribute(Qt.WidgetAttribute.WA_TransparentForMouseEvents, enabled)
        if sys.platform == "win32":
            self._set_windows_click_through(enabled)
        self.update()
        self.click_through_changed.emit(enabled)

    def set_locked(self, enabled: bool, *, local: bool = True) -> None:
        if local:
            self._remote_interaction_enabled = False
        enabled = bool(enabled)
        if self._locked == enabled:
            return
        self._locked = enabled
        self.set_click_through(enabled, local=False)
        self.controls.setVisible(not enabled)
        self._layout_children()
        self.lock_changed.emit(enabled)

    def unlock_for_editing(self) -> None:
        self.set_locked(False, local=True)
        self.set_click_through(False, local=True)
        self.controls.show()
        self._layout_children()
        self.show()
        self.raise_()

    def follow_remote_interaction(self) -> None:
        self._remote_interaction_enabled = True

    def follow_remote_geometry(self) -> None:
        self._local_geometry_override = False
        self.geometry_override_changed.emit(False)

    def resizeEvent(self, _event: Any) -> None:
        self._layout_children()

    def paintEvent(self, _event: Any) -> None:
        if self._click_through:
            return
        painter = QPainter(self)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)
        border = QColor(125, 211, 252, 120)
        painter.setPen(QPen(border, 1.5))
        painter.setBrush(Qt.BrushStyle.NoBrush)
        painter.drawRoundedRect(QRectF(self.rect()).adjusted(1, 1, -2, -2), 10, 10)
        grip = QRectF(self.width() - 24, self.height() - 24, 16, 16)
        cc = QColor(self._control_color)
        cc.setAlpha(max(cc.alpha() - 6, 0))
        painter.setPen(QPen(cc, 2))
        painter.drawLine(grip.left(), grip.bottom(), grip.right(), grip.top())
        painter.drawLine(grip.left() + 6, grip.bottom(), grip.right(), grip.top() + 6)

    def _layout_children(self) -> None:
        controls_visible = not self._locked and not self.controls.isHidden()
        controls_height = 0
        toolbar_height = 42
        if controls_visible:
            panel_height = 46 if not self.style_panel.isHidden() else 0
            controls_height = toolbar_height + panel_height + (4 if panel_height else 0)
            self.controls.setGeometry(8, 8, max(1, self.width() - 16), controls_height)
            self.toolbar.setFixedHeight(toolbar_height)
            if panel_height:
                self.style_panel.setFixedHeight(panel_height)
            self.controls.raise_()

        top = toolbar_height + 10 if controls_visible else 0
        self.canvas.setGeometry(0, top, self.width(), max(1, self.height() - top))

    def mousePressEvent(self, event: Any) -> None:
        if self._click_through or event.button() != Qt.MouseButton.LeftButton:
            return
        self._drag_global = _global_pos(event)
        self._drag_geometry = self.geometry()
        self._drag_mode = "resize" if self._in_resize_zone(event.position().toPoint()) else "move"
        event.accept()

    def mouseMoveEvent(self, event: Any) -> None:
        if self._click_through:
            return
        pos = event.position().toPoint()
        if not self._drag_mode:
            self.setCursor(Qt.CursorShape.SizeFDiagCursor if self._in_resize_zone(pos) else Qt.CursorShape.SizeAllCursor)
            return

        delta = _global_pos(event) - self._drag_global
        if self._drag_mode == "resize":
            new_width = max(280, self._drag_geometry.width() + delta.x())
            new_height = max(80, self._drag_geometry.height() + delta.y())
            self.resize(QSize(new_width, new_height))
        elif self._drag_mode == "move":
            self.move(self._drag_geometry.topLeft() + delta)
        event.accept()

    def mouseReleaseEvent(self, event: Any) -> None:
        if self._drag_mode in {"resize", "move"}:
            self._local_geometry_override = True
            self.geometry_override_changed.emit(True)
        self._drag_mode = None
        event.accept()

    def mouseDoubleClickEvent(self, event: Any) -> None:
        if event.button() == Qt.MouseButton.LeftButton:
            self.set_locked(True, local=True)
            event.accept()

    def _clear_status(self) -> None:
        if self._status_active:
            self.clear_subtitle()

    def _in_resize_zone(self, pos: QPoint) -> bool:
        return pos.x() >= self.width() - self._resize_margin and pos.y() >= self.height() - self._resize_margin

    def _place_default(self) -> None:
        screen = QApplication.primaryScreen()
        if not screen:
            return
        available = screen.availableGeometry()
        width = min(self.width(), int(available.width() * 0.86))
        height = self.height()
        self.resize(width, height)
        x = available.left() + (available.width() - width) // 2
        y = available.bottom() - height - 80
        self.move(x, y)

    def _set_windows_click_through(self, enabled: bool) -> None:
        hwnd = int(self.winId())
        user32 = ctypes.windll.user32
        gwl_exstyle = -20
        ws_ex_layered = 0x00080000
        ws_ex_transparent = 0x00000020
        swp_nosize = 0x0001
        swp_nomove = 0x0002
        swp_nozorder = 0x0004
        swp_framechanged = 0x0020
        swp_noactivate = 0x0010

        exstyle = user32.GetWindowLongW(hwnd, gwl_exstyle)
        exstyle |= ws_ex_layered
        if enabled:
            exstyle |= ws_ex_transparent
        else:
            exstyle &= ~ws_ex_transparent
        user32.SetWindowLongW(hwnd, gwl_exstyle, exstyle)
        user32.SetWindowPos(
            hwnd,
            0,
            0,
            0,
            0,
            0,
            swp_nomove | swp_nosize | swp_nozorder | swp_noactivate | swp_framechanged,
        )


def _wrap_text(text: str, font: QFont, max_width: int, max_lines: int) -> list[str]:
    text = clean_text(text)
    if not text or max_lines <= 0:
        return []

    metrics = QFontMetrics(font)
    lines: list[str] = []
    for paragraph in text.splitlines():
        tokens = _tokenize(paragraph)
        current = ""
        for token in tokens:
            candidate = f"{current}{token}"
            if current and metrics.horizontalAdvance(candidate.rstrip()) > max_width:
                lines.append(current.rstrip())
                if len(lines) >= max_lines:
                    return _ellipsize_last(lines, metrics, max_width)
                current = token.lstrip()
            else:
                current = candidate
        if current:
            lines.append(current.rstrip())
            if len(lines) >= max_lines:
                return _ellipsize_last(lines, metrics, max_width)
    return lines


def _tokenize(paragraph: str) -> list[str]:
    if re.search(r"\s", paragraph):
        return re.findall(r"\S+\s*", paragraph)
    return list(paragraph)


def _ellipsize_last(lines: list[str], metrics: QFontMetrics, max_width: int) -> list[str]:
    if not lines:
        return lines
    ellipsis = "..."
    last = lines[-1]
    while last and metrics.horizontalAdvance(f"{last}{ellipsis}") > max_width:
        last = last[:-1]
    lines[-1] = f"{last}{ellipsis}" if last else ellipsis
    return lines

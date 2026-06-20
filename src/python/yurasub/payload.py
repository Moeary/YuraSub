from __future__ import annotations

from typing import Any

MAX_TEXT_LENGTH = 1200


def clean_text(value: Any) -> str:
    if value is None:
        return ""
    text = str(value).replace("\r\n", "\n").replace("\r", "\n")
    lines = [" ".join(line.split()) for line in text.split("\n")]
    return "\n".join(line for line in lines if line).strip()[:MAX_TEXT_LENGTH]


def read_bool(value: Any, default: bool | None = None) -> bool | None:
    if isinstance(value, bool):
        return value
    if isinstance(value, (int, float)):
        return bool(value)
    if isinstance(value, str):
        lowered = value.strip().lower()
        if lowered in {"1", "true", "yes", "on"}:
            return True
        if lowered in {"0", "false", "no", "off"}:
            return False
    return default


def pick_text(payload: dict[str, Any]) -> tuple[str, str]:
    text = clean_text(
        payload.get("text")
        or payload.get("subtitle")
        or payload.get("caption")
        or payload.get("lyric")
        or payload.get("primary")
    )
    translation = clean_text(
        payload.get("translation")
        or payload.get("translated")
        or payload.get("secondary")
        or payload.get("extra")
    )
    return text, translation


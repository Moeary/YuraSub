"""Portable JSON configuration for YuraSub.

Config file lives next to the executable (frozen/exe mode) or at the
repository root when running from source (``pixi run start``).
No registry writes, no %APPDATA%, no user directory.

Resolution priority:
  1. ``--config`` CLI argument
  2. ``YURASUB_CONFIG`` environment variable
  3. Default path:
     - Source mode: repo root (directory containing ``pixi.toml``)
     - Frozen/exe mode: directory containing the real ``.exe``
"""

from __future__ import annotations

import json
import logging
import os
import sys
import tempfile
from pathlib import Path
from typing import Any

logger = logging.getLogger(__name__)

CONFIG_SCHEMA_VERSION = 1
DEFAULT_CONFIG_FILENAME = "config.json"
LEGACY_CONFIG_FILENAME = "YuraSub.config.json"

DEFAULT_CONFIG: dict[str, Any] = {
    "schemaVersion": CONFIG_SCHEMA_VERSION,
    "server": {
        "websocketPort": 8765,
        "httpPort": 8766,
    },
    "window": {
        "x": None,
        "y": None,
        "width": 1100,
        "height": 180,
        "clickThrough": False,
    },
    "style": {
        "fontFamily": "Microsoft YaHei UI",
        "fontSize": 34,
        "translationFontSize": 24,
        "textColor": "#ffffff",
        "textOpacity": 100,
        "outlineColor": "#101522",
        "outlineWidth": 4,
        "outlineOpacity": 100,
        "controlColor": "#f5fff8e6",
        "controlOpacity": 90,
        "backgroundColor": "#00000000",
        "backgroundOpacity": 0,
    },
}


def _find_repo_root() -> Path | None:
    """Walk up from this file looking for ``pixi.toml``.

    Returns the directory containing ``pixi.toml``, or ``None`` if not
    found (e.g. installed as a package without source tree).
    """
    here = Path(__file__).resolve()
    for parent in here.parents:
        if (parent / "pixi.toml").exists():
            return parent
    return None


def _default_dir() -> Path:
    """Return the directory for the default config file.

    Source mode (pixi.toml found): repo root.
    Frozen/exe mode: directory containing the real executable.
    Fallback: current working directory.
    """
    # Frozen build (Nuitka onefile or PyInstaller).
    if getattr(sys, "frozen", False):
        return Path(sys.argv[0]).resolve().parent

    # Source / dev mode — find the repo root via pixi.toml.
    repo = _find_repo_root()
    if repo is not None:
        return repo

    # Fallback: if sys.executable is not in a temp dir, use its parent.
    exe = Path(sys.executable).resolve()
    try:
        exe.relative_to(Path(tempfile.gettempdir()))
    except ValueError:
        return exe.parent

    return Path.cwd()


def resolve_config_path(override: str | None = None) -> Path:
    """Determine the config file path.

    Priority:
      1. *override* (from ``--config``)
      2. ``YURASUB_CONFIG`` environment variable
      3. Default directory + ``YuraSub.config.json``
    """
    if override:
        return Path(override).resolve()
    env = os.environ.get("YURASUB_CONFIG")
    if env:
        return Path(env).resolve()
    return _default_dir() / DEFAULT_CONFIG_FILENAME


def _deep_merge(base: dict[str, Any], overrides: dict[str, Any]) -> dict[str, Any]:
    """Merge *overrides* into a copy of *base*.

    Only top-level keys are merged; nested dicts are replaced wholesale
    when present in *overrides*.
    """
    merged = dict(base)
    for key, value in overrides.items():
        if isinstance(value, dict) and isinstance(merged.get(key), dict):
            merged[key] = _deep_merge(merged[key], value)
        else:
            merged[key] = value
    return merged


def _migrate_legacy_config(target: Path) -> dict[str, Any] | None:
    """If *target* doesn't exist but the legacy file does, migrate it.

    Reads the legacy ``YuraSub.config.json`` from the same directory,
    saves it as ``config.json``, and deletes the legacy file.
    Returns the loaded config dict, or ``None`` if no migration needed.
    """
    if target.exists():
        return None
    legacy = target.parent / LEGACY_CONFIG_FILENAME
    if not legacy.exists():
        return None
    try:
        text = legacy.read_text(encoding="utf-8")
        data = json.loads(text)
    except (json.JSONDecodeError, OSError) as exc:
        logger.warning("Failed to read legacy config %s: %s", legacy, exc)
        return None
    if not isinstance(data, dict):
        return None
    # Save to new location.
    try:
        save_config(target, data)
    except OSError as exc:
        logger.warning("Failed to migrate config to %s: %s", target, exc)
        return data
    # Delete legacy file.
    try:
        legacy.unlink()
        logger.info("Migrated %s -> %s", legacy.name, target.name)
    except OSError:
        pass
    return data


def load_config(path: Path) -> dict[str, Any]:
    """Load config from *path*, filling missing keys from defaults.

    Returns a deep copy of defaults merged with the file contents.
    If the file is missing or malformed, returns defaults unchanged.
    Unknown keys (e.g. legacy ``backgroundColor``) are preserved but
    do not cause errors.

    Transparently migrates ``YuraSub.config.json`` → ``config.json``
    if the new file doesn't exist yet.
    """
    data = _migrate_legacy_config(path)
    if data is None:
        if not path.exists():
            return _deep_merge(DEFAULT_CONFIG, {})
        try:
            text = path.read_text(encoding="utf-8")
            data = json.loads(text)
        except (json.JSONDecodeError, OSError) as exc:
            logger.warning("Failed to read config %s: %s — using defaults", path, exc)
            return _deep_merge(DEFAULT_CONFIG, {})
        if not isinstance(data, dict):
            logger.warning("Config %s is not a JSON object — using defaults", path)
            return _deep_merge(DEFAULT_CONFIG, {})
    return _deep_merge(DEFAULT_CONFIG, data)


def save_config(path: Path, config: dict[str, Any]) -> None:
    """Atomically write *config* to *path*.

    Writes to a temporary file in the same directory, then renames.
    Creates parent directories if needed.
    """
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, tmp = tempfile.mkstemp(
        suffix=".tmp",
        prefix=".config_",
        dir=str(path.parent),
    )
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as f:
            json.dump(config, f, ensure_ascii=False, indent=2)
            f.write("\n")
        # Atomic rename (Windows: os.replace is atomic on same volume).
        os.replace(tmp, path)
    except OSError:
        # Clean up temp file on failure.
        try:
            os.unlink(tmp)
        except OSError:
            pass
        raise

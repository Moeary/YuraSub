"""Tests for the portable JSON configuration module."""

from __future__ import annotations

import json
import os
from pathlib import Path

import pytest

from yurasub.config import (
    CONFIG_SCHEMA_VERSION,
    DEFAULT_CONFIG,
    DEFAULT_CONFIG_FILENAME,
    LEGACY_CONFIG_FILENAME,
    _deep_merge,
    _default_dir,
    _find_repo_root,
    _migrate_legacy_config,
    load_config,
    resolve_config_path,
    save_config,
)


# ---------------------------------------------------------------------------
# Defaults
# ---------------------------------------------------------------------------


def test_default_config_has_required_sections():
    assert "schemaVersion" in DEFAULT_CONFIG
    assert "server" in DEFAULT_CONFIG
    assert "window" in DEFAULT_CONFIG
    assert "style" in DEFAULT_CONFIG


def test_default_ports():
    assert DEFAULT_CONFIG["server"]["websocketPort"] == 8765
    assert DEFAULT_CONFIG["server"]["httpPort"] == 8766


def test_default_style_matches_overlay():
    """Config defaults should match the overlay's DEFAULT_STYLE."""
    style = DEFAULT_CONFIG["style"]
    assert style["fontFamily"] == "Microsoft YaHei UI"
    assert style["fontSize"] == 34
    assert style["translationFontSize"] == 24
    assert style["textColor"] == "#ffffff"
    assert style["textOpacity"] == 100
    assert style["outlineColor"] == "#101522"
    assert style["outlineWidth"] == 4
    assert style["outlineOpacity"] == 100
    assert style["controlColor"] == "#f5fff8e6"
    assert style["controlOpacity"] == 90
    assert style["backgroundColor"] == "#00000000"
    assert style["backgroundOpacity"] == 0


def test_schema_version_is_int():
    assert isinstance(CONFIG_SCHEMA_VERSION, int)
    assert CONFIG_SCHEMA_VERSION >= 1


def test_default_filename_is_config_json():
    assert DEFAULT_CONFIG_FILENAME == "config.json"


def test_legacy_filename_preserved():
    assert LEGACY_CONFIG_FILENAME == "YuraSub.config.json"


# ---------------------------------------------------------------------------
# load_config
# ---------------------------------------------------------------------------


def test_load_config_missing_file_returns_defaults(tmp_path: Path):
    result = load_config(tmp_path / "nonexistent.json")
    assert result == DEFAULT_CONFIG


def test_load_config_valid_json_merges_with_defaults(tmp_path: Path):
    cfg = tmp_path / "config.json"
    cfg.write_text(json.dumps({"server": {"websocketPort": 9999}}), encoding="utf-8")
    result = load_config(cfg)
    assert result["server"]["websocketPort"] == 9999
    assert result["server"]["httpPort"] == DEFAULT_CONFIG["server"]["httpPort"]
    assert result["window"] == DEFAULT_CONFIG["window"]
    assert result["style"] == DEFAULT_CONFIG["style"]


def test_load_config_partial_style_fills_defaults(tmp_path: Path):
    cfg = tmp_path / "config.json"
    cfg.write_text(json.dumps({"style": {"fontSize": 48}}), encoding="utf-8")
    result = load_config(cfg)
    assert result["style"]["fontSize"] == 48
    assert result["style"]["fontFamily"] == DEFAULT_CONFIG["style"]["fontFamily"]


def test_load_config_malformed_json_returns_defaults(tmp_path: Path):
    cfg = tmp_path / "bad.json"
    cfg.write_text("not json {{{", encoding="utf-8")
    result = load_config(cfg)
    assert result == DEFAULT_CONFIG


def test_load_config_non_dict_json_returns_defaults(tmp_path: Path):
    cfg = tmp_path / "list.json"
    cfg.write_text("[1, 2, 3]", encoding="utf-8")
    result = load_config(cfg)
    assert result == DEFAULT_CONFIG


def test_load_config_empty_file_returns_defaults(tmp_path: Path):
    cfg = tmp_path / "empty.json"
    cfg.write_text("", encoding="utf-8")
    result = load_config(cfg)
    assert result == DEFAULT_CONFIG


def test_load_config_legacy_background_color_not_lost(tmp_path: Path):
    """Old config with backgroundColor should load without error."""
    cfg = tmp_path / "config.json"
    cfg.write_text(
        json.dumps({"style": {"backgroundColor": "#ff000080", "backgroundOpacity": 50}}),
        encoding="utf-8",
    )
    result = load_config(cfg)
    assert result["style"]["backgroundColor"] == "#ff000080"
    assert result["style"]["backgroundOpacity"] == 50
    assert result["style"]["controlColor"] == DEFAULT_CONFIG["style"]["controlColor"]


def test_load_config_control_color(tmp_path: Path):
    cfg = tmp_path / "config.json"
    cfg.write_text(json.dumps({"style": {"controlColor": "#aabbccdd"}}), encoding="utf-8")
    result = load_config(cfg)
    assert result["style"]["controlColor"] == "#aabbccdd"


# ---------------------------------------------------------------------------
# Legacy migration (YuraSub.config.json → config.json)
# ---------------------------------------------------------------------------


def test_migrate_legacy_creates_new_and_deletes_old(tmp_path: Path):
    legacy = tmp_path / LEGACY_CONFIG_FILENAME
    target = tmp_path / DEFAULT_CONFIG_FILENAME
    legacy.write_text(json.dumps({"server": {"websocketPort": 1111}}), encoding="utf-8")
    data = _migrate_legacy_config(target)
    assert data is not None
    assert data["server"]["websocketPort"] == 1111
    assert target.exists()
    assert not legacy.exists()


def test_migrate_legacy_skipped_when_target_exists(tmp_path: Path):
    legacy = tmp_path / LEGACY_CONFIG_FILENAME
    target = tmp_path / DEFAULT_CONFIG_FILENAME
    target.write_text("{}", encoding="utf-8")
    legacy.write_text('{"server":{"websocketPort":9999}}', encoding="utf-8")
    data = _migrate_legacy_config(target)
    assert data is None
    assert legacy.exists()  # not deleted


def test_migrate_legacy_skipped_when_no_legacy(tmp_path: Path):
    target = tmp_path / DEFAULT_CONFIG_FILENAME
    data = _migrate_legacy_config(target)
    assert data is None


def test_load_config_transparently_migrates_legacy(tmp_path: Path):
    """load_config should read from legacy file transparently."""
    legacy = tmp_path / LEGACY_CONFIG_FILENAME
    target = tmp_path / DEFAULT_CONFIG_FILENAME
    legacy.write_text(json.dumps({"server": {"httpPort": 7777}}), encoding="utf-8")
    result = load_config(target)
    assert result["server"]["httpPort"] == 7777
    assert target.exists()
    assert not legacy.exists()


# ---------------------------------------------------------------------------
# save_config
# ---------------------------------------------------------------------------


def test_save_config_creates_file(tmp_path: Path):
    cfg = tmp_path / "out.json"
    save_config(cfg, DEFAULT_CONFIG)
    assert cfg.exists()
    data = json.loads(cfg.read_text(encoding="utf-8"))
    assert data["schemaVersion"] == CONFIG_SCHEMA_VERSION


def test_save_config_round_trips(tmp_path: Path):
    cfg = tmp_path / "rt.json"
    modified = dict(DEFAULT_CONFIG)
    modified["server"] = {"websocketPort": 1234, "httpPort": 5678}
    save_config(cfg, modified)
    loaded = load_config(cfg)
    assert loaded["server"]["websocketPort"] == 1234
    assert loaded["server"]["httpPort"] == 5678


def test_save_config_creates_parent_dirs(tmp_path: Path):
    cfg = tmp_path / "sub" / "deep" / "config.json"
    save_config(cfg, DEFAULT_CONFIG)
    assert cfg.exists()


# ---------------------------------------------------------------------------
# resolve_config_path — priority
# ---------------------------------------------------------------------------


def test_resolve_config_path_cli_override_wins(monkeypatch, tmp_path: Path):
    custom = tmp_path / "custom.json"
    monkeypatch.setenv("YURASUB_CONFIG", str(tmp_path / "env.json"))
    result = resolve_config_path(str(custom))
    assert result == custom.resolve()


def test_resolve_config_path_env_var(monkeypatch, tmp_path: Path):
    env_path = tmp_path / "env.json"
    monkeypatch.setenv("YURASUB_CONFIG", str(env_path))
    result = resolve_config_path(None)
    assert result == env_path.resolve()


def test_resolve_config_path_default(monkeypatch, tmp_path: Path):
    monkeypatch.delenv("YURASUB_CONFIG", raising=False)
    result = resolve_config_path(None)
    assert result.name == "config.json"


# ---------------------------------------------------------------------------
# Default directory detection (dev mode vs frozen)
# ---------------------------------------------------------------------------


def test_find_repo_root_finds_pixi_toml():
    root = _find_repo_root()
    assert root is not None
    assert (root / "pixi.toml").exists()


def test_default_dir_is_repo_root_in_dev_mode():
    d = _default_dir()
    assert (d / "pixi.toml").exists(), f"Expected pixi.toml in {d}"
    path_str = str(d).lower()
    assert ".pixi" not in path_str, f"Config should not be in .pixi: {d}"


def test_resolve_config_path_default_not_in_pixi_env(monkeypatch):
    monkeypatch.delenv("YURASUB_CONFIG", raising=False)
    result = resolve_config_path(None)
    path_str = str(result).lower()
    assert ".pixi" not in path_str, f"Config path inside .pixi: {result}"


def test_resolve_config_path_default_at_repo_root(monkeypatch):
    monkeypatch.delenv("YURASUB_CONFIG", raising=False)
    result = resolve_config_path(None)
    assert result.parent == _find_repo_root()


# ---------------------------------------------------------------------------
# _deep_merge
# ---------------------------------------------------------------------------


def test_deep_merge_base_only():
    result = _deep_merge({"a": 1, "b": 2}, {})
    assert result == {"a": 1, "b": 2}


def test_deep_merge_override_wins():
    result = _deep_merge({"a": 1, "b": 2}, {"b": 99})
    assert result == {"a": 1, "b": 99}


def test_deep_merge_nested_dict():
    base = {"section": {"x": 1, "y": 2}}
    over = {"section": {"y": 99}}
    result = _deep_merge(base, over)
    assert result == {"section": {"x": 1, "y": 99}}


def test_deep_merge_does_not_mutate_base():
    base = {"a": {"x": 1}}
    over = {"a": {"x": 2}}
    _deep_merge(base, over)
    assert base["a"]["x"] == 1


# ---------------------------------------------------------------------------
# CLI arg override integration
# ---------------------------------------------------------------------------


def test_cli_port_overrides_config():
    from yurasub.app import build_parser

    parser = build_parser()
    args = parser.parse_args(["--port", "9001"])
    config = {"server": {"websocketPort": 8765, "httpPort": 8766}}
    server_cfg = config.setdefault("server", {})
    if args.port is not None:
        server_cfg["websocketPort"] = args.port
    assert server_cfg["websocketPort"] == 9001
    assert server_cfg["httpPort"] == 8766


def test_cli_no_override_keeps_config_value():
    from yurasub.app import build_parser

    parser = build_parser()
    args = parser.parse_args([])
    config = {"server": {"websocketPort": 9999, "httpPort": 5555}}
    server_cfg = config.setdefault("server", {})
    if args.port is not None:
        server_cfg["websocketPort"] = args.port
    if args.http_port is not None:
        server_cfg["httpPort"] = args.http_port
    assert server_cfg["websocketPort"] == 9999
    assert server_cfg["httpPort"] == 5555


def test_config_path_not_in_user_directory(tmp_path: Path):
    cfg = tmp_path / "config.json"
    save_config(cfg, DEFAULT_CONFIG)
    resolved = cfg.resolve()
    path_str = str(resolved).lower()
    assert "appdata" not in path_str
    assert "users\\" not in path_str or str(tmp_path).lower().split("users\\")[0] in path_str

from __future__ import annotations

import os
import sys
from pathlib import Path

_DLL_DIRECTORY_HANDLES: list[object] = []
_PREPARED = False


def prepare_qt_runtime() -> None:
    global _PREPARED
    if _PREPARED or os.name != "nt":
        return

    prefixes = [Path(sys.prefix), Path(sys.base_prefix)]
    dll_candidates: list[Path] = []
    plugin_candidates: list[Path] = []
    for prefix in prefixes:
        dll_candidates.extend(
            [
                prefix / "Library" / "bin",
                prefix / "Library" / "lib" / "qt6" / "bin",
                prefix / "Lib" / "site-packages" / "PySide6",
            ]
        )
        plugin_candidates.extend(
            [
                prefix / "Library" / "lib" / "qt6" / "plugins",
                prefix / "Library" / "plugins",
                prefix / "Lib" / "site-packages" / "PySide6" / "plugins",
            ]
        )

    existing_dll_dirs = _dedupe(path for path in dll_candidates if path.exists())
    existing_plugin_dirs = _dedupe(path for path in plugin_candidates if path.exists())

    for path in existing_dll_dirs:
        if hasattr(os, "add_dll_directory"):
            _DLL_DIRECTORY_HANDLES.append(os.add_dll_directory(str(path)))

    path_value = os.environ.get("PATH", "")
    prefix_value = os.pathsep.join(str(path) for path in existing_dll_dirs)
    if prefix_value and prefix_value not in path_value:
        os.environ["PATH"] = f"{prefix_value}{os.pathsep}{path_value}" if path_value else prefix_value

    if existing_plugin_dirs:
        plugin_path_value = os.environ.get("QT_PLUGIN_PATH", "")
        plugin_prefix = os.pathsep.join(str(path) for path in existing_plugin_dirs)
        if plugin_prefix and plugin_prefix not in plugin_path_value:
            os.environ["QT_PLUGIN_PATH"] = (
                f"{plugin_prefix}{os.pathsep}{plugin_path_value}" if plugin_path_value else plugin_prefix
            )

        platform_dir = next((path / "platforms" for path in existing_plugin_dirs if (path / "platforms").exists()), None)
        if platform_dir is not None:
            if not os.environ.get("QT_QPA_PLATFORM_PLUGIN_PATH"):
                os.environ["QT_QPA_PLATFORM_PLUGIN_PATH"] = str(platform_dir)

    _PREPARED = True


def _dedupe(paths: object) -> list[Path]:
    result: list[Path] = []
    seen: set[str] = set()
    for path in paths:
        resolved = str(Path(path).resolve()).lower()
        if resolved in seen:
            continue
        seen.add(resolved)
        result.append(Path(path))
    return result

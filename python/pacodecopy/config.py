"""Application paths and user settings (shared with the C# version)."""

from __future__ import annotations

import json
import os
import sys
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

APP_FOLDER_NAME = "PaCodeCopy"
MIN_DETECTION_THRESHOLD = 1
MAX_DETECTION_THRESHOLD = 20


def _documents_folder() -> Path:
    """Same folder as .NET Environment.SpecialFolder.MyDocuments (follows OneDrive redirection)."""
    if sys.platform == "win32":
        import ctypes
        from ctypes import wintypes

        csidl_personal = 5
        buffer = ctypes.create_unicode_buffer(wintypes.MAX_PATH)
        if ctypes.windll.shell32.SHGetFolderPathW(None, csidl_personal, None, 0, buffer) == 0:
            return Path(buffer.value)
    return Path.home() / "Documents"


def _local_app_data() -> Path:
    if sys.platform == "win32" and os.environ.get("LOCALAPPDATA"):
        return Path(os.environ["LOCALAPPDATA"])
    return Path(os.environ.get("XDG_DATA_HOME", Path.home() / ".local" / "share"))


@dataclass(frozen=True)
class AppPaths:
    data_folder: Path
    default_snippets_root: Path

    @property
    def database_path(self) -> Path:
        return self.data_folder / "index.db"

    @property
    def settings_path(self) -> Path:
        return self.data_folder / "settings.json"

    @property
    def log_folder(self) -> Path:
        return self.data_folder / "logs"

    @classmethod
    def default(cls) -> "AppPaths":
        return cls(
            data_folder=_local_app_data() / APP_FOLDER_NAME,
            default_snippets_root=_documents_folder() / APP_FOLDER_NAME / "Snippets",
        )


@dataclass
class AppSettings:
    """User settings. JSON keys are PascalCase to match the C# app's settings.json."""

    snippets_root_folder: str
    auto_capture: bool = True
    detection_threshold: int = 3
    default_category: str = "General"
    minimize_to_tray: bool = True
    show_notifications: bool = True
    log_retention_days: int = 14
    # Keys written by the other app that this one does not know; kept when saving.
    extra: dict[str, Any] = field(default_factory=dict)

    _KEYS = {
        "SnippetsRootFolder": "snippets_root_folder",
        "AutoCapture": "auto_capture",
        "DetectionThreshold": "detection_threshold",
        "DefaultCategory": "default_category",
        "MinimizeToTray": "minimize_to_tray",
        "ShowNotifications": "show_notifications",
        "LogRetentionDays": "log_retention_days",
    }

    def normalize(self, default_root: Path) -> None:
        if not str(self.snippets_root_folder or "").strip():
            self.snippets_root_folder = str(default_root)
        if not str(self.default_category or "").strip():
            self.default_category = "General"
        self.detection_threshold = min(max(int(self.detection_threshold), MIN_DETECTION_THRESHOLD), MAX_DETECTION_THRESHOLD)
        self.log_retention_days = min(max(int(self.log_retention_days), 1), 365)

    def to_json(self) -> dict[str, Any]:
        data = dict(self.extra)
        for json_key, attr in self._KEYS.items():
            data[json_key] = getattr(self, attr)
        return data

    @classmethod
    def from_json(cls, data: dict[str, Any], default_root: Path) -> "AppSettings":
        settings = cls(snippets_root_folder=str(default_root))
        for json_key, value in data.items():
            attr = cls._KEYS.get(json_key)
            if attr is None:
                settings.extra[json_key] = value
            else:
                setattr(settings, attr, value)
        settings.normalize(default_root)
        return settings


class SettingsStore:
    """Loads/saves settings.json. A missing or broken file gives defaults."""

    def __init__(self, path: Path, default_root: Path, logger) -> None:
        self._path = path
        self._default_root = default_root
        self._logger = logger

    def load(self) -> AppSettings:
        try:
            if self._path.exists():
                # utf-8-sig: the C# app may write a BOM.
                data = json.loads(self._path.read_text(encoding="utf-8-sig"))
                if isinstance(data, dict):
                    return AppSettings.from_json(data, self._default_root)
        except (OSError, ValueError) as ex:
            self._logger.warning("Could not read settings from '%s'. Using defaults. (%s)", self._path, ex)
        settings = AppSettings(snippets_root_folder=str(self._default_root))
        settings.normalize(self._default_root)
        return settings

    def save(self, settings: AppSettings) -> None:
        settings.normalize(self._default_root)
        self._path.parent.mkdir(parents=True, exist_ok=True)
        temp = self._path.with_suffix(".json.tmp")
        temp.write_text(json.dumps(settings.to_json(), indent=2), encoding="utf-8")
        os.replace(temp, self._path)

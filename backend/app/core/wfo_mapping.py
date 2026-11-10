"""Accepted text values for WFO uploads, loaded from a JSON config file (never hard-coded).

Default file: ``backend/config/wfo_mapping.json``; override with ``WFO_MAPPING_FILE`` (absolute, or
relative to ``backend/``). Matching ignores case, surrounding/extra spaces, hyphens and underscores,
so "On-site", "on site" and "ON_SITE" are the same value.
"""

from __future__ import annotations

import json
import os
import re
from dataclasses import dataclass
from functools import lru_cache
from pathlib import Path

from app.core.config import BACKEND_DIR
from app.models.enums import WorkMode

DEFAULT_FILE = BACKEND_DIR / "config" / "wfo_mapping.json"
COLUMN_KEYS = ("employee", "name", "date", "work_mode", "hours", "in_time", "out_time", "source")


def normalize(text: str | None) -> str:
    return re.sub(r"[\s_\-]+", " ", (text or "").strip().lower()).strip()


@dataclass(frozen=True)
class WfoMapping:
    work_modes: dict[str, WorkMode]          # normalized text -> work mode
    badge_sources: frozenset[str]            # normalized source values meaning badge / swipe
    columns: dict[str, str]                  # normalized header -> canonical column key

    def work_mode(self, text: str | None) -> WorkMode | None:
        return self.work_modes.get(normalize(text))

    def is_badge(self, text: str | None) -> bool:
        return normalize(text) in self.badge_sources

    def column(self, header: str | None) -> str | None:
        return self.columns.get(normalize(header))


def mapping_file() -> Path:
    raw = os.environ.get("WFO_MAPPING_FILE", "").strip()
    if not raw:
        return DEFAULT_FILE
    path = Path(raw)
    return path if path.is_absolute() else BACKEND_DIR / path


def parse_mapping(data: dict) -> WfoMapping:
    """Validates the file: unknown work modes or a value mapped to two modes are configuration errors."""
    modes: dict[str, WorkMode] = {}
    for mode_name, values in (data.get("work_modes") or {}).items():
        try:
            mode = WorkMode(mode_name)
        except ValueError:
            raise ValueError(f"wfo_mapping: unknown work mode {mode_name!r} (use {[m.value for m in WorkMode]})") from None
        for value in values:
            key = normalize(value)
            if key in modes and modes[key] != mode:
                raise ValueError(f"wfo_mapping: {value!r} is listed for both {modes[key]} and {mode}")
            modes[key] = mode
        modes.setdefault(normalize(mode.value), mode)  # the canonical name always works
    columns: dict[str, str] = {}
    for key, aliases in (data.get("columns") or {}).items():
        if key not in COLUMN_KEYS:
            raise ValueError(f"wfo_mapping: unknown column {key!r} (use {list(COLUMN_KEYS)})")
        for alias in [key, *aliases]:
            columns.setdefault(normalize(alias), key)
    return WfoMapping(work_modes=modes, badge_sources=frozenset(normalize(v) for v in data.get("badge_sources") or []),
                      columns=columns)


@lru_cache(maxsize=1)
def get_mapping() -> WfoMapping:
    path = mapping_file()
    try:
        return parse_mapping(json.loads(path.read_text(encoding="utf-8")))
    except FileNotFoundError:
        raise RuntimeError(f"WFO mapping file not found: {path}") from None

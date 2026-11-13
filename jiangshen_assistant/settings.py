"""Validated local settings with atomic writes and preserved invalid inputs."""

from __future__ import annotations

from dataclasses import asdict, dataclass, field
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import tempfile
import uuid

from .account_profiles import DAILY_CATALOG, RETIRED_DAILY_KEYS, SHOP_DURATIONS


@dataclass
class AppSettings:
    window_keyword: str = "将神"
    window_title: str = ""
    preview_interval_ms: int = 0  # Legacy setting retained for old files; no screenshot preview uses it.
    stop_hotkey: str = "F8"
    daily_items: dict[str, bool] = field(default_factory=lambda: {key: key == 'online_gift' for key in DAILY_CATALOG})
    daily_donation_type: int = 2
    daily_shop_duration: int = 3600


class SettingsStore:
    SCHEMA_VERSION = 1

    def __init__(self, data_dir: Path) -> None:
        self.data_dir = Path(data_dir)
        self.path = self.data_dir / "settings.json"
        self.last_warning = ""
        self._preserved_raw: bytes | None = None

    @staticmethod
    def _validate(values: dict) -> AppSettings:
        defaults = AppSettings()
        keyword = values.get("window_keyword", defaults.window_keyword)
        title = values.get("window_title", defaults.window_title)
        interval = values.get("preview_interval_ms", defaults.preview_interval_ms)
        hotkey = values.get("stop_hotkey", defaults.stop_hotkey)
        daily_items = values.get("daily_items", defaults.daily_items)
        donation = values.get("daily_donation_type", defaults.daily_donation_type)
        shop_duration = values.get("daily_shop_duration", defaults.daily_shop_duration)
        if not isinstance(keyword, str) or len(keyword) > 256:
            raise ValueError("window_keyword 必须是最多 256 个字符的文本")
        if not isinstance(title, str) or len(title) > 1024:
            raise ValueError("window_title 必须是最多 1024 个字符的文本")
        if type(interval) is not int or not 0 <= interval <= 60000:
            raise ValueError("preview_interval_ms 必须是 0 到 60000 的整数")
        if hotkey != "F8" or not isinstance(hotkey, str):
            raise ValueError("当前版本的 stop_hotkey 仅支持 F8")
        if (type(daily_items) is not dict or set(daily_items) - (set(DAILY_CATALOG) | RETIRED_DAILY_KEYS)
                or any(type(value) is not bool for value in daily_items.values())):
            raise ValueError("daily_items 必须是有效日常子项到布尔开关的映射")
        if type(donation) is not int or donation not in (1, 2, 3):
            raise ValueError("daily_donation_type 必须是 1、2 或 3")
        if type(shop_duration) is not int or shop_duration not in SHOP_DURATIONS:
            raise ValueError("daily_shop_duration 无效")
        return AppSettings(keyword, title, interval, hotkey,
                           {key: daily_items.get(key, key == 'online_gift') for key in DAILY_CATALOG}, donation, shop_duration)

    @classmethod
    def _decode(cls, raw: bytes) -> AppSettings:
        document = json.loads(raw.decode("utf-8-sig"))
        if not isinstance(document, dict):
            raise ValueError("配置根节点必须是对象")
        version = document.get("schema_version")
        if type(version) is not int or version != cls.SCHEMA_VERSION:
            raise ValueError(f"不支持的配置版本：{version!r}")
        return cls._validate(document)

    def _preserve(self, raw: bytes) -> Path | None:
        if self._preserved_raw == raw:
            return None
        stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S%fZ")
        backup = self.data_dir / f"settings.invalid-{stamp}-{uuid.uuid4().hex[:8]}.json"
        with backup.open("xb") as stream:
            stream.write(raw)
            stream.flush()
            os.fsync(stream.fileno())
        self._preserved_raw = raw
        return backup

    def load(self) -> AppSettings:
        self.last_warning = ""
        try:
            raw = self.path.read_bytes()
        except FileNotFoundError:
            return AppSettings()
        except OSError as exc:
            self.last_warning = f"无法读取配置，使用默认值：{exc}"
            return AppSettings()
        try:
            return self._decode(raw)
        except (ValueError, UnicodeError, RecursionError) as exc:
            self.last_warning = f"配置无效，使用默认值：{exc}。原文件保持不变。"
            try:
                backup = self._preserve(raw)
                if backup is not None:
                    self.last_warning += f" 已备份至 {backup.name}。"
            except OSError as backup_error:
                self.last_warning += f" 备份失败：{backup_error}。"
            return AppSettings()

    def save(self, settings: AppSettings) -> None:
        if not isinstance(settings, AppSettings):
            raise TypeError("settings 必须是 AppSettings")
        validated = self._validate(asdict(settings))
        document = {"schema_version": self.SCHEMA_VERSION, **asdict(validated)}
        payload = json.dumps(document, ensure_ascii=False, indent=2) + "\n"
        self.data_dir.mkdir(parents=True, exist_ok=True)
        try:
            existing = self.path.read_bytes()
        except FileNotFoundError:
            existing = None
        if existing is not None:
            try:
                self._decode(existing)
            except (ValueError, UnicodeError, RecursionError):
                # Refuse to overwrite the only original if preservation fails.
                self._preserve(existing)
        temporary: str | None = None
        try:
            with tempfile.NamedTemporaryFile(
                mode="w", encoding="utf-8", newline="\n", dir=self.data_dir,
                prefix=".settings-", suffix=".tmp", delete=False,
            ) as stream:
                temporary = stream.name
                stream.write(payload)
                stream.flush()
                os.fsync(stream.fileno())
            os.replace(temporary, self.path)
        finally:
            if temporary is not None:
                Path(temporary).unlink(missing_ok=True)

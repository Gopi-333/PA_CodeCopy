"""Daily log files next to the C# app's logs (prefix pacodecopy-py-)."""

from __future__ import annotations

import logging
import time
from datetime import datetime
from pathlib import Path

LOGGER_NAME = "pacodecopy"
_PREFIX = "pacodecopy-py-"


def setup_logging(folder: Path, retention_days: int) -> logging.Logger:
    """Logs to <folder>/pacodecopy-py-YYYYMMDD.log. Logging problems never stop the app."""
    logger = logging.getLogger(LOGGER_NAME)
    logger.setLevel(logging.INFO)
    if logger.handlers:
        return logger

    try:
        folder.mkdir(parents=True, exist_ok=True)
        _delete_old_files(folder, retention_days)
        handler: logging.Handler = logging.FileHandler(
            folder / f"{_PREFIX}{datetime.now():%Y%m%d}.log", encoding="utf-8", delay=True
        )
    except OSError:
        handler = logging.NullHandler()

    handler.setFormatter(logging.Formatter("%(asctime)s [%(levelname)-5s] %(message)s"))
    logger.addHandler(handler)
    return logger


def _delete_old_files(folder: Path, retention_days: int) -> None:
    cutoff = time.time() - retention_days * 86400
    for file in folder.glob(f"{_PREFIX}*.log"):
        try:
            if file.stat().st_mtime < cutoff:
                file.unlink()
        except OSError:
            pass

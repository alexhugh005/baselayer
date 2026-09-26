"""Loggers that print to the terminal and append to $DATA_ROOT/logs/<name>.log."""

from __future__ import annotations

import logging
import sys

from .paths import logs_root

FORMAT = "[%(asctime)s] %(message)s"


def get_logger(name):
    """Logger for one job; the file handler is added once per log path."""
    logger = logging.getLogger(f"baselayer_data.{name}")
    logger.setLevel(logging.INFO)
    logger.propagate = False
    path = logs_root() / f"{name}.log"
    if not any(getattr(h, "baseFilename", None) == str(path) for h in logger.handlers):
        for handler in list(logger.handlers):
            logger.removeHandler(handler)
            handler.close()
        path.parent.mkdir(parents=True, exist_ok=True)
        for handler in (logging.StreamHandler(sys.stdout), logging.FileHandler(path)):
            handler.setFormatter(logging.Formatter(FORMAT, "%Y-%m-%d %H:%M:%S"))
            logger.addHandler(handler)
    return logger

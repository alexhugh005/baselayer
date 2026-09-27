"""Load a training run directory for serving.

Only three files from train.py's output are needed: model.json,
run_config.json and classification_thresholds.json.
"""

from __future__ import annotations

import hashlib
import json
from dataclasses import dataclass
from pathlib import Path

import xgboost as xgb

from baselayer_model.austin_outage import modeling as m

CONFIG_FILE = "run_config.json"
DEFAULT_DIR = Path(__file__).resolve().parent / "model"


@dataclass(frozen=True)
class Run:
    model: xgb.XGBClassifier
    features: list[str]
    int_features: frozenset[str]
    thresholds: dict
    config: dict
    version: str


def load_run(run_dir=DEFAULT_DIR):
    run_dir = Path(run_dir)
    config = json.loads((run_dir / CONFIG_FILE).read_text())
    thresholds = json.loads((run_dir / m.THRESHOLDS_FILE).read_text())

    # Serving builds only features known before the day starts.
    if config.get("feature_set") != "day_ahead":
        raise ValueError(f"serving needs a day_ahead run, got {config.get('feature_set')!r}")
    if config.get("classes") != m.CLASSES:
        raise ValueError(f"run classes {config.get('classes')} differ from {m.CLASSES}")
    trained = str(config.get("xgboost_version", ""))
    if trained.split(".")[:2] != xgb.__version__.split(".")[:2]:
        raise ValueError(f"run was trained with xgboost {trained}, this is {xgb.__version__}")

    model = xgb.XGBClassifier()
    model.load_model(run_dir / m.MODEL_FILE)
    # Trained on CUDA; serve on CPU.
    model.set_params(device="cpu")
    booster = model.get_booster()
    if list(booster.feature_names or []) != config["features"]:
        raise ValueError("model.json feature names differ from run_config.json features")

    digest = hashlib.sha256((run_dir / m.MODEL_FILE).read_bytes()).hexdigest()[:12]
    name = Path(config.get("out_dir") or run_dir).name
    return Run(
        model=model,
        features=list(config["features"]),
        int_features=frozenset(
            f for f, t in zip(booster.feature_names, booster.feature_types or []) if t == "int"
        ),
        thresholds=thresholds,
        config=config,
        version=f"{name}-{digest}",
    )

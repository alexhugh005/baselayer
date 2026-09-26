"""Shared pieces for training and evaluating the outage risk classifier.

Input contract (baselayer_data's `austin-outage/hourly` dataset): one row per
operating hour with

    oper_day      date (Central time) the row belongs to
    hour_ending   1-24 (ERCOT convention)
    label         "low" | "medium" | "high", constant within a day
    event         high-event name (uri_2021, mara_2023, microburst_2025), or empty
    <features>    every other numeric column

Columns in META_COLS are never used as features.
"""

from __future__ import annotations

import json
import logging
from pathlib import Path

import numpy as np
import pandas as pd
from sklearn.metrics import (
    average_precision_score,
    balanced_accuracy_score,
    classification_report,
    confusion_matrix,
    f1_score,
    fbeta_score,
    log_loss,
)

log = logging.getLogger(__name__)

CLASSES = ["low", "medium", "high"]
CLASS_TO_ID = {c: i for i, c in enumerate(CLASSES)}
LOW, MEDIUM, HIGH = range(3)

META_COLS = {
    "oper_day",
    "hour_ending",
    "label",
    "event",
    "source",
    "note",
    "dst_flag",
    "interval_end_utc",
    "group",
    # Data-coverage counts from features.py, not grid signals.
    "price_intervals",
    "sced_runs",
}
# Labeled days closer than this are one episode, so lag features never
# straddle a train/validation boundary.
EPISODE_GAP_DAYS = 3

DATASET = "austin-outage/hourly"
THRESHOLDS_FILE = "classification_thresholds.json"
MODEL_FILE = "model.json"
SPLIT_FILE = "split.json"


# --------------------------------------------------------------------------- data


def default_data_path():
    from baselayer_data import dataset_path

    return dataset_path(DATASET)


def load_table(path):
    path = Path(path)
    df = pd.read_csv(path) if path.suffix == ".csv" else pd.read_parquet(path)
    missing = {"oper_day", "label"} - set(df.columns)
    if missing:
        raise ValueError(f"{path} is missing required columns: {sorted(missing)}")
    df["oper_day"] = pd.to_datetime(df["oper_day"]).dt.date
    df["label"] = df["label"].str.lower()
    unknown = set(df["label"]) - set(CLASSES)
    if unknown:
        raise ValueError(f"unknown labels: {sorted(unknown)}")
    if "event" not in df.columns:
        df["event"] = None
    df["group"] = assign_groups(df)
    order = ["oper_day", "hour_ending"] if "hour_ending" in df else ["oper_day"]
    return df.sort_values(order).reset_index(drop=True)


def feature_columns(df, drop=()):
    cols = [
        c
        for c in df.columns
        if c not in META_COLS
        and c not in set(drop)
        and pd.api.types.is_numeric_dtype(df[c])
        and not pd.api.types.is_bool_dtype(df[c])
    ]
    if not cols:
        raise ValueError("no numeric feature columns found")
    return cols


def assign_groups(df):
    """Group id per row: the high event name, else a run of nearby labeled days.

    Grouping keeps every hour of a day, and neighbouring days that share lag
    features, on the same side of any split.
    """
    days = df[["oper_day", "event"]].drop_duplicates("oper_day").sort_values("oper_day")
    group_of, episode, prev = {}, 0, None
    for day, event in days.itertuples(index=False):
        if prev is not None and (day - prev).days > EPISODE_GAP_DAYS:
            episode += 1
        group_of[day] = event if isinstance(event, str) and event else f"episode_{episode}"
        prev = day
    return df["oper_day"].map(group_of)


def group_labels(df):
    """Highest label in each group (high > medium > low)."""
    y = df["label"].map(CLASS_TO_ID)
    return y.groupby(df["group"]).max()


# ------------------------------------------------------------------------- splits


def make_test_split(df, test_event=None, test_frac=0.15, seed=0):
    """Pick held-out groups: one whole high event plus a stratified slice of the rest.

    Returns the set of test group ids. The test event defaults to the most
    recent high event so the test set mimics forecasting an unseen storm.
    """
    glabel = group_labels(df)
    high_groups = sorted(
        glabel[glabel == HIGH].index,
        key=lambda g: df.loc[df["group"] == g, "oper_day"].min(),
    )
    if len(high_groups) < 3:
        raise ValueError(
            f"need >= 3 high events (1 test + 2 for leave-one-event-out CV), got {high_groups}"
        )
    test_event = test_event or high_groups[-1]
    if test_event not in high_groups:
        raise ValueError(f"test event {test_event!r} not in {high_groups}")

    rng = np.random.default_rng(seed)
    test = {test_event}
    for cls in (LOW, MEDIUM):
        groups = sorted(glabel[glabel == cls].index)
        n = int(round(len(groups) * test_frac))
        test.update(rng.choice(groups, size=n, replace=False).tolist())
    return test


def make_cv_folds(df, seed=0):
    """Leave-one-event-out folds: each high event is its own validation fold.

    Non-high groups are dealt round-robin (shuffled, per class) across the
    folds so every fold also validates on some medium and low days.
    Returns a list of (train_idx, valid_idx) positional index arrays.
    """
    glabel = group_labels(df)
    high_groups = sorted(glabel[glabel == HIGH].index)
    k = len(high_groups)
    if k < 2:
        raise ValueError("need >= 2 high events outside the test set for CV")

    rng = np.random.default_rng(seed)
    fold_of = {g: i for i, g in enumerate(high_groups)}
    for cls in (MEDIUM, LOW):
        groups = list(glabel[glabel == cls].index)
        rng.shuffle(groups)
        fold_of.update({g: i % k for i, g in enumerate(groups)})

    fold = df["group"].map(fold_of).to_numpy()
    return [(np.where(fold != i)[0], np.where(fold == i)[0]) for i in range(k)]


# -------------------------------------------------------------------- aggregation


def daily_probs(df, proba):
    """Collapse hourly probabilities to one row per day by averaging.

    Labels are constant within a day, so every hour is a noisy vote for the
    day's class; the mean keeps a proper distribution (sums to 1).
    p_elevated = p_medium + p_high is the "at least medium" score.
    """
    hourly = pd.DataFrame(
        {
            "oper_day": df["oper_day"].to_numpy(),
            "label": df["label"].to_numpy(),
            "p_low": proba[:, LOW],
            "p_medium": proba[:, MEDIUM],
            "p_high": proba[:, HIGH],
        }
    )
    daily = hourly.groupby("oper_day").agg(
        label=("label", "first"),
        p_low=("p_low", "mean"),
        p_medium=("p_medium", "mean"),
        p_high=("p_high", "mean"),
    )
    daily["p_elevated"] = daily["p_medium"] + daily["p_high"]
    daily["y"] = daily["label"].map(CLASS_TO_ID)
    return daily.reset_index()


def daily_proba_matrix(daily):
    return daily[["p_low", "p_medium", "p_high"]].to_numpy()


# --------------------------------------------------------------------- thresholds


def _best_threshold(y_true, score, beta):
    """Threshold on `score` that maximises F-beta for the binary target."""
    if y_true.sum() == 0:
        return 0.5, 0.0
    candidates = np.unique(score)
    fs = np.array([fbeta_score(y_true, score >= t, beta=beta, zero_division=0) for t in candidates])
    # Ties go to the higher threshold (fewer false alarms).
    i = int(np.flatnonzero(fs == fs.max())[-1])
    # Sit halfway into the gap below the chosen score: same OOF predictions,
    # but not balanced exactly on one training day's probability.
    t = candidates[i] if i == 0 else (candidates[i] + candidates[i - 1]) / 2
    return float(t), float(fs[i])


def fit_thresholds(daily, beta_high=2.0, beta_medium=1.0):
    """Tune per-class thresholds on out-of-fold daily probabilities.

    Decision rule (cascade, checked in order):
      high    if p_high     >= thresholds.high
      medium  if p_elevated >= thresholds.medium   (p_elevated = p_medium + p_high)
      low     otherwise, i.e. p_low > thresholds.low where low = 1 - medium
    """
    y = daily["y"].to_numpy()
    t_high, f_high = _best_threshold((y == HIGH).astype(int), daily["p_high"].to_numpy(), beta_high)
    t_med, f_med = _best_threshold(
        (y >= MEDIUM).astype(int), daily["p_elevated"].to_numpy(), beta_medium
    )
    return {
        "thresholds": {
            "low": round(1.0 - t_med, 6),
            "medium": round(t_med, 6),
            "high": round(t_high, 6),
        },
        "tuning": {
            "high": {"score": "p_high", "objective": f"F{beta_high:g}", "oof_value": round(f_high, 4)},
            "medium": {
                "score": "p_elevated",
                "objective": f"F{beta_medium:g}",
                "oof_value": round(f_med, 4),
            },
        },
    }


def apply_thresholds(daily, thresholds):
    t = thresholds.get("thresholds", thresholds)
    pred = np.full(len(daily), LOW)
    pred[daily["p_elevated"].to_numpy() >= t["medium"]] = MEDIUM
    pred[daily["p_high"].to_numpy() >= t["high"]] = HIGH
    return pred


def thresholds_document(fitted, **extra):
    return {
        "classes": CLASSES,
        "unit": "operating_day",
        "hourly_aggregation": "mean of hourly class probabilities over the operating day",
        "decision_rule": [
            "high if p_high >= thresholds.high",
            "medium if p_medium + p_high >= thresholds.medium",
            "low otherwise (equivalently p_low > thresholds.low)",
        ],
        **fitted,
        **extra,
    }


# ------------------------------------------------------------------------ metrics


def classification_metrics(y_true, pred, proba=None):
    """Metrics dict for 3-class predictions; `proba` is (n, 3) if given."""
    labels = list(range(len(CLASSES)))
    out = {
        "n": int(len(y_true)),
        "support": {c: int((y_true == i).sum()) for i, c in enumerate(CLASSES)},
        "macro_f1": float(f1_score(y_true, pred, labels=labels, average="macro", zero_division=0)),
        "balanced_accuracy": float(balanced_accuracy_score(y_true, pred)),
        "confusion_matrix": confusion_matrix(y_true, pred, labels=labels).tolist(),
        "per_class": classification_report(
            y_true, pred, labels=labels, target_names=CLASSES, output_dict=True, zero_division=0
        ),
    }
    if proba is not None:
        proba = np.clip(proba, 1e-7, 1)
        proba = proba / proba.sum(axis=1, keepdims=True)
        out["log_loss"] = float(log_loss(y_true, proba, labels=labels))
        out["pr_auc"] = {
            c: float(average_precision_score(y_true == i, proba[:, i])) if (y_true == i).any() else None
            for i, c in enumerate(CLASSES)
        }
    return out


def format_metrics(name, m):
    pc = m["per_class"]
    lines = [
        f"{name}: n={m['n']} support={m['support']}",
        f"  macro_f1={m['macro_f1']:.3f}  balanced_acc={m['balanced_accuracy']:.3f}"
        + (f"  log_loss={m['log_loss']:.3f}" if "log_loss" in m else ""),
    ]
    for c in CLASSES:
        auc = m.get("pr_auc", {}).get(c)
        lines.append(
            f"  {c:<6} precision={pc[c]['precision']:.3f} recall={pc[c]['recall']:.3f} "
            f"f1={pc[c]['f1-score']:.3f}" + (f" pr_auc={auc:.3f}" if auc is not None else "")
        )
    lines.append("  confusion (rows=true low/medium/high, cols=pred):")
    lines += [f"    {row}" for row in m["confusion_matrix"]]
    return "\n".join(lines)


def write_json(path, obj):
    Path(path).write_text(json.dumps(obj, indent=2, default=str) + "\n")

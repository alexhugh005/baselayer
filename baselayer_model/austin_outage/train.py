"""Train the Austin outage risk classifier (XGBoost, 3 classes).

    python -m baselayer_model.austin_outage.train [--feature-set day_ahead|nowcast]

Holds out a test split (one whole high event plus ~15% of other day groups)
that this script never scores, runs leave-one-event-out CV on the rest while
logging metrics every boosting round, tunes per-class decision thresholds on
the out-of-fold predictions, and refits on all non-test rows. Everything is
written to a fresh temp directory (or --out-dir). Score the held-out split
afterwards with `python -m baselayer_model.austin_outage.evaluate --run-dir <dir>`.
"""

from __future__ import annotations

import argparse
import logging
import tempfile
import time
import warnings
from pathlib import Path

import numpy as np
import pandas as pd
import xgboost as xgb
from sklearn.utils.class_weight import compute_sample_weight

from baselayer_model.austin_outage import modeling as m

log = logging.getLogger("baselayer_model.austin_outage.train")


def parse_args(argv=None):
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--data", help="hourly feature table, parquet or .csv (default: $DATA_ROOT/dataset/austin-outage/hourly.parq)")
    p.add_argument("--out-dir", help="output directory (default: new temp dir)")
    p.add_argument("--device", choices=["auto", "cuda", "cpu"], default="auto")
    p.add_argument("--test-event", help="high event to hold out for test (default: most recent)")
    p.add_argument("--test-frac", type=float, default=0.15, help="share of low/medium groups held out")
    p.add_argument("--seed", type=int, default=20260926)
    p.add_argument(
        "--feature-set", choices=sorted(m.FEATURE_SETS), default=m.DEFAULT_FEATURE_SET,
        help="day_ahead: only data known before the day starts (default); "
        "nowcast: also same-day actual load, forecast error, price and lambda",
    )
    p.add_argument("--drop-cols", nargs="*", default=[], help="numeric columns to exclude as features")
    p.add_argument("--beta-high", type=float, default=2.0, help="F-beta for the high threshold (recall-leaning)")
    p.add_argument("--beta-medium", type=float, default=1.0)
    p.add_argument("--log-every", type=int, default=25, help="print eval metrics every N rounds")
    p.add_argument("--n-jobs", type=int, help="CPU threads (default: all cores)")
    # XGBoost hyperparameters, sized for a few thousand hourly rows.
    p.add_argument("--n-estimators", type=int, default=2000)
    p.add_argument("--early-stopping", type=int, default=100)
    p.add_argument("--learning-rate", type=float, default=0.03)
    p.add_argument("--max-depth", type=int, default=4)
    p.add_argument("--min-child-weight", type=float, default=5.0)
    p.add_argument("--subsample", type=float, default=0.8)
    p.add_argument("--colsample-bytree", type=float, default=0.8)
    p.add_argument("--reg-lambda", type=float, default=1.0)
    return p.parse_args(argv)


def resolve_device(requested):
    """Use CUDA when asked for (or on auto) and it actually works, else CPU."""
    if requested == "cpu":
        return "cpu"
    if xgb.build_info().get("USE_CUDA"):
        X = np.random.default_rng(0).normal(size=(30, 2))
        y = np.arange(30) % 3
        try:
            with warnings.catch_warnings(record=True) as caught:
                warnings.simplefilter("always")
                xgb.XGBClassifier(n_estimators=1, device="cuda", tree_method="hist").fit(X, y)
            # A CUDA build with no visible GPU warns and silently runs on CPU.
            if not any("cpu" in str(w.message).lower() for w in caught):
                return "cuda"
        except xgb.core.XGBoostError as exc:
            log.warning("CUDA probe failed: %s", exc)
    if requested == "cuda":
        log.warning("CUDA requested but unavailable; falling back to CPU")
    return "cpu"


def make_model(args, device, n_estimators=None, early_stopping=True):
    return xgb.XGBClassifier(
        objective="multi:softprob",
        num_class=len(m.CLASSES),
        tree_method="hist",
        device=device,
        n_estimators=n_estimators or args.n_estimators,
        learning_rate=args.learning_rate,
        max_depth=args.max_depth,
        min_child_weight=args.min_child_weight,
        subsample=args.subsample,
        colsample_bytree=args.colsample_bytree,
        reg_lambda=args.reg_lambda,
        # Early stopping watches the last metric listed.
        eval_metric=["merror", "mlogloss"],
        early_stopping_rounds=args.early_stopping if early_stopping else None,
        n_jobs=args.n_jobs,
        random_state=args.seed,
    )


def weights(y):
    """Balanced class weights so 192 high hours count as much as ~8,000 low ones."""
    return compute_sample_weight("balanced", y)


def check_classes(y, where):
    missing = [m.CLASSES[i] for i in range(len(m.CLASSES)) if not (y == i).any()]
    if missing:
        raise ValueError(f"{where} has no rows for class(es) {missing}")


def run_cv(df, X, y, args, device):
    folds = m.make_cv_folds(df, seed=args.seed)
    oof = np.full((len(df), len(m.CLASSES)), np.nan)
    fold_reports, best_iters = [], []

    for k, (tr, va) in enumerate(folds):
        held = sorted(g for g in df["group"].iloc[va].unique() if not g.startswith("episode_"))
        log.info(
            "CV fold %d/%d: train=%d rows, valid=%d rows, validation events=%s",
            k + 1, len(folds), len(tr), len(va), held,
        )
        check_classes(y[tr], f"fold {k + 1} train")

        model = make_model(args, device)
        t0 = time.time()
        model.fit(
            X.iloc[tr], y[tr], sample_weight=weights(y[tr]),
            eval_set=[(X.iloc[tr], y[tr]), (X.iloc[va], y[va])],
            sample_weight_eval_set=[weights(y[tr]), weights(y[va])],
            verbose=args.log_every,
        )
        best = int(model.best_iteration)
        best_iters.append(best + 1)
        proba = model.predict_proba(X.iloc[va], iteration_range=(0, best + 1))
        oof[va] = proba

        hourly = m.classification_metrics(y[va], proba.argmax(1), proba)
        daily = m.daily_probs(df.iloc[va], proba)
        dproba = m.daily_proba_matrix(daily)
        daily_m = m.classification_metrics(daily["y"].to_numpy(), dproba.argmax(1), dproba)
        log.info(
            "fold %d best_iteration=%d (%.1fs)\n%s\n%s", k + 1, best, time.time() - t0,
            m.format_metrics(f"fold {k + 1} hourly (argmax)", hourly),
            m.format_metrics(f"fold {k + 1} daily (argmax)", daily_m),
        )
        fold_reports.append({
            "fold": k + 1, "validation_events": held, "best_iteration": best,
            "hourly": hourly, "daily_argmax": daily_m,
        })
    return oof, fold_reports, best_iters


def main(argv=None):
    args = parse_args(argv)
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
    data = Path(args.data) if args.data else m.default_data_path()

    out = Path(args.out_dir) if args.out_dir else Path(tempfile.mkdtemp(prefix="austin_outage_run_"))
    out.mkdir(parents=True, exist_ok=True)
    log.info("outputs -> %s", out)

    df = m.load_table(data)
    features = m.feature_columns(df, args.drop_cols, args.feature_set)
    log.info(
        "%d rows, %d days, %d features (feature set %s)",
        len(df), df["oper_day"].nunique(), len(features), args.feature_set,
    )

    test_groups = m.make_test_split(df, args.test_event, args.test_frac, args.seed)
    is_test = df["group"].isin(test_groups).to_numpy()
    test_days = sorted(df.loc[is_test, "oper_day"].unique())
    m.write_json(out / m.SPLIT_FILE, {
        "test_groups": sorted(test_groups),
        "test_days": [str(d) for d in test_days],
        "test_label_counts_days": df[is_test].drop_duplicates("oper_day")["label"].value_counts().to_dict(),
    })
    # The test rows leave here and are never touched again in this script.
    dev = df[~is_test].reset_index(drop=True)
    log.info(
        "held out %d test days (%d rows) in groups %s; training on %d days",
        len(test_days), is_test.sum(),
        sorted(g for g in test_groups if not g.startswith("episode_")), dev["oper_day"].nunique(),
    )

    X = dev[features]
    y = dev["label"].map(m.CLASS_TO_ID).to_numpy()
    check_classes(y, "training split")

    device = resolve_device(args.device)
    log.info("xgboost %s on device=%s", xgb.__version__, device)

    # 1. Leave-one-event-out CV -> out-of-fold probabilities.
    oof, fold_reports, best_iters = run_cv(dev, X, y, args, device)

    # 2. Thresholds tuned on the OOF daily scores.
    oof_daily = m.daily_probs(dev, oof)
    fitted = m.fit_thresholds(oof_daily, args.beta_high, args.beta_medium)
    oof_y, oof_proba = oof_daily["y"].to_numpy(), m.daily_proba_matrix(oof_daily)
    oof_argmax = m.classification_metrics(oof_y, oof_proba.argmax(1), oof_proba)
    oof_thresh = m.classification_metrics(oof_y, m.apply_thresholds(oof_daily, fitted), oof_proba)
    log.info("thresholds: %s", fitted["thresholds"])
    log.info(
        "\n%s\n%s",
        m.format_metrics("OOF daily (argmax)", oof_argmax),
        m.format_metrics("OOF daily (tuned thresholds)", oof_thresh),
    )

    # 3. Refit on every non-test row for the CV-average number of rounds.
    n_rounds = max(1, int(round(np.mean(best_iters))))
    log.info("refitting on %d rows for %d rounds", len(dev), n_rounds)
    final = make_model(args, device, n_estimators=n_rounds, early_stopping=False)
    final.fit(
        X, y, sample_weight=weights(y), eval_set=[(X, y)],
        sample_weight_eval_set=[weights(y)], verbose=args.log_every,
    )
    final.save_model(out / m.MODEL_FILE)

    m.write_json(out / m.THRESHOLDS_FILE, m.thresholds_document(
        fitted, tuned_on="leave-one-event-out out-of-fold predictions (test split excluded)",
    ))
    m.write_json(out / "train_metrics.json", {
        "folds": fold_reports,
        "oof_daily_argmax": oof_argmax,
        "oof_daily_thresholds": oof_thresh,
        "final_n_estimators": n_rounds,
    })
    m.write_json(out / "run_config.json", {
        **vars(args), "data": str(data.resolve()), "out_dir": str(out),
        "device_used": device, "xgboost_version": xgb.__version__, "features": features,
        "classes": m.CLASSES,
    })
    oof_daily.to_csv(out / "oof_daily_predictions.csv", index=False)
    pd.Series(final.feature_importances_, index=features, name="gain") \
        .sort_values(ascending=False).to_csv(out / "feature_importance.csv")

    log.info(
        "done. evaluate the held-out split with:\n"
        "  python -m baselayer_model.austin_outage.evaluate --run-dir %s", out,
    )
    return out


if __name__ == "__main__":
    main()

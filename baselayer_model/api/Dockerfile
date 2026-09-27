# Austin outage risk model API.
# Build from the repository root (it needs baselayer_data and baselayer_model):
#   docker build -f baselayer_model/api/Dockerfile -t baselayer-model .
FROM python:3.12-slim

# xgboost needs the OpenMP runtime.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgomp1 \
    && rm -rf /var/lib/apt/lists/*

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    DATA_ROOT=/data \
    MODEL_DIR=/app/model

WORKDIR /app
COPY baselayer_model/api/requirements.txt /tmp/requirements.txt
RUN pip install --no-cache-dir -r /tmp/requirements.txt

COPY baselayer_data/ baselayer_data/
COPY baselayer_model/__init__.py baselayer_model/
COPY baselayer_model/austin_outage/ baselayer_model/austin_outage/
COPY baselayer_model/api/*.py baselayer_model/api/
COPY baselayer_model/api/demo/__init__.py baselayer_model/api/demo/demo_cases.json baselayer_model/api/demo/
# The run directory served; pass another train.py output to swap models.
ARG MODEL_RUN=baselayer_model/api/model
COPY ${MODEL_RUN}/model.json ${MODEL_RUN}/run_config.json ${MODEL_RUN}/classification_thresholds.json /app/model/

RUN useradd --create-home --uid 10001 app && mkdir -p /data && chown app /data
USER app
VOLUME /data
EXPOSE 8000

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD python -c "import urllib.request; urllib.request.urlopen('http://127.0.0.1:8000/health', timeout=4)"

CMD ["uvicorn", "baselayer_model.api.app:app", "--host", "0.0.0.0", "--port", "8000"]

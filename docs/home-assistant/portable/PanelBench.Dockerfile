FROM python:3.14-slim
RUN apt-get update && apt-get install -y --no-install-recommends mosquitto mosquitto-clients && \
    rm -rf /var/lib/apt/lists/*
WORKDIR /app
ADD https://codeload.github.com/SpanPanel/panelbench/tar.gz/ffdb5472be5b14e5346394edffb8bbd23a6f7b72 /tmp/panelbench.tar.gz
RUN tar -xzf /tmp/panelbench.tar.gz -C /app --strip-components=1 && \
    pip install --no-cache-dir . && rm /tmp/panelbench.tar.gz && \
    mkdir -p /app/certs /app/configs /mosquitto/data
COPY span-panel/entrypoint.sh span-panel/live_panel.py span-panel/mqtt_receiver.py span-panel/circuits.json /lab/
COPY span-panel/energy-lab.yaml /lab/energy-lab.yaml
COPY portable/start-panelbench.sh /lab/start-panelbench.sh
ENTRYPOINT ["/bin/sh", "/lab/start-panelbench.sh"]

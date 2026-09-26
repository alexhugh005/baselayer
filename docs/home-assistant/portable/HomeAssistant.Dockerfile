FROM ghcr.io/home-assistant/home-assistant:2026.9.3

# Public source only: no saved accounts, tokens, registry, or history from the backup.
ADD https://codeload.github.com/SpanPanel/span/tar.gz/54749b90a4f83e539efb5c085d1cb1d8449d24f9 /tmp/span.tar.gz
RUN mkdir -p /tmp/span /opt/lab/custom_components && \
    tar -xzf /tmp/span.tar.gz -C /tmp/span --strip-components=1 && \
    cp -r /tmp/span/custom_components/span_panel /opt/lab/custom_components/ && \
    rm -rf /tmp/span /tmp/span.tar.gz
RUN uv pip install --system 'span-panel-api==3.4.1' \
    'span-panel-api-schema-0==1.1.2' 'span-panel-api-schema-1==1.1.3'
COPY portable/patch_frontend.py /opt/lab/patch_frontend.py
RUN python3 /opt/lab/patch_frontend.py
COPY realistic-lab/packages/ /opt/lab/packages/
COPY realistic-lab/dashboard.yaml /opt/lab/energy-lab.yaml
COPY portable/configuration.yaml /opt/lab/configuration.yaml
COPY portable/energy_lab_bootstrap/ /opt/lab/custom_components/energy_lab_bootstrap/
COPY span-panel/circuits.json span-panel/relays.json /opt/lab/custom_components/energy_lab_bootstrap/
COPY portable/seed.py portable/start-homeassistant.sh /opt/lab/
ENTRYPOINT ["/bin/sh", "/opt/lab/start-homeassistant.sh"]

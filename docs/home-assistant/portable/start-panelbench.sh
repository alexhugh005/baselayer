#!/bin/sh
set -eu
if [ ! -f /app/configs/energy-lab.yaml ]; then
    cp /lab/energy-lab.yaml /app/configs/energy-lab.yaml
fi
echo "Waiting for Home Assistant to create the local bridge credential..."
while [ ! -s /run/secrets/ha-token ]; do sleep 2; done
exec /bin/bash /lab/entrypoint.sh

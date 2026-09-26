#!/bin/sh
set -eu
python3 /opt/lab/seed.py
rm -f /config/.energy-lab-ready
exec /init

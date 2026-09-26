"""Build a small shareable ZIP from an explicit public-file allowlist."""
from pathlib import Path
import zipfile

ROOT = Path(__file__).resolve().parents[1]
LAB = ROOT / 'docs/home-assistant'
OUTPUT = ROOT / 'artifacts/energy-lab/energy-lab-compose.zip'


def main():
    files = [(ROOT / 'compose.energy-lab.yaml', 'compose.yaml'),
             (LAB / 'portable/README.md', 'README.md'),
             (LAB / '.dockerignore', 'docs/home-assistant/.dockerignore'),
             (LAB / 'realistic-lab/dashboard.yaml', 'docs/home-assistant/realistic-lab/dashboard.yaml')]
    for path in sorted((LAB / 'portable').rglob('*')):
        if path.is_file() and path.suffix in ('.py', '.sh', '.json', '.yaml', '.md', '.Dockerfile'):
            files.append((path, path.relative_to(ROOT).as_posix()))
    for path in sorted((LAB / 'realistic-lab/packages').glob('*.yaml')):
        files.append((path, path.relative_to(ROOT).as_posix()))
    for name in ('entrypoint.sh', 'live_panel.py', 'mqtt_receiver.py', 'circuits.json', 'relays.json', 'energy-lab.yaml'):
        path = LAB / 'span-panel' / name
        files.append((path, path.relative_to(ROOT).as_posix()))
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(OUTPUT, 'w', compression=zipfile.ZIP_DEFLATED) as archive:
        for source, target in files:
            archive.write(source, target)
    print(f'{OUTPUT}: {len(files)} files, {OUTPUT.stat().st_size:,} bytes')


if __name__ == '__main__':
    main()

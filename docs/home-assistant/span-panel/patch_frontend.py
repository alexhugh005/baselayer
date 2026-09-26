"""Fix the pinned SPAN 2.1.2 settings drawer's asynchronous checked reflection.

span-switch changes its checked property and emits change synchronously; Lit
reflects the checked attribute later. Reading attribute OR property in that
event therefore turns an OFF click back into an ON service call.
"""
from pathlib import Path
import argparse
import shutil

OLD = 'const t=a.hasAttribute("checked")||a.checked;'
NEW = 'const t=a.checked;'


def patch(config):
    folder = config / 'custom_components/span_panel/frontend/dist'
    for name in ('span-panel.js', 'span-panel-card.js'):
        path = folder / name
        content = path.read_text()
        if OLD not in content and NEW in content:
            print(name + ': already patched')
            continue
        if content.count(OLD) != 1:
            raise RuntimeError('Upstream frontend changed; review patch: ' + str(path))
        backup = Path(__file__).resolve().parents[3] / '.local/span-panel/frontend-backup'
        backup.mkdir(parents=True, exist_ok=True)
        if not (backup / name).exists():
            shutil.copy2(path, backup / name)
        path.write_text(content.replace(OLD, NEW))
        print(name + ': patched breaker change handler')


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('config', type=Path)
    patch(parser.parse_args().config)

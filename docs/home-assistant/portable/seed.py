"""Seed an empty dedicated volume; never merge a personal HA backup into it."""
import shutil
from pathlib import Path


def seed(source: Path, target: Path):
    target.mkdir(parents=True, exist_ok=True)
    marker = target / '.energy-lab-seeded'
    if marker.exists():
        return
    if (target / 'configuration.yaml').exists() or (target / '.storage').exists():
        raise RuntimeError('Refusing to overwrite an existing Home Assistant configuration. Use a fresh named volume.')
    for directory in ('packages', 'custom_components'):
        shutil.copytree(source / directory, target / directory, dirs_exist_ok=True)
    shutil.copyfile(source / 'configuration.yaml', target / 'configuration.yaml')
    dashboard = (source / 'energy-lab.yaml').read_text().replace('Johnson Household', 'Energy Lab')
    for name in ('energy-lab.yaml', 'ui-lovelace.yaml'):
        (target / name).write_text(dashboard)
    marker.write_text('1\n')


if __name__ == '__main__':
    seed(Path('/opt/lab'), Path('/config'))

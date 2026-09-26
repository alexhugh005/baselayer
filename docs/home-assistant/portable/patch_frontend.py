"""Apply the existing SPAN 2.1.2 breaker-toggle fix to the image's clean seed."""
from pathlib import Path

old = 'const t=a.hasAttribute("checked")||a.checked;'
new = 'const t=a.checked;'
for name in ('span-panel.js', 'span-panel-card.js'):
    path = Path('/opt/lab/custom_components/span_panel/frontend/dist') / name
    text = path.read_text()
    if old not in text and new in text:
        continue
    if text.count(old) != 1:
        raise RuntimeError(f'Pinned frontend changed: {name}')
    path.write_text(text.replace(old, new))

"""Validate local Markdown targets (including explicit/GitHub-style anchors)."""
from pathlib import Path
from urllib.parse import unquote
import re
import hashlib
root = Path(__file__).resolve().parents[1]
errors = []
count = 0
for file in [root / 'README.md', *sorted((root / 'docs').rglob('*.md'))]:
    body = re.sub(r'^```.*?^```', '', file.read_text(encoding='utf-8'), flags=re.M | re.S)
    for match in re.finditer(r'\[[^\]\n]*\]\(([^)\n]+)\)', body):
        target = match[1].strip('<>')
        if re.match(r'[a-zA-Z]+:', target):
            continue
        name, _, anchor = unquote(target).partition('#')
        dest = (file.parent / name).resolve() if name else file
        count += 1
        if not dest.exists():
            errors.append(f'{file.relative_to(root)}: missing {target}')
        elif anchor and dest.suffix == '.md':
            content = dest.read_text(encoding='utf-8')
            anchors = set(re.findall(r'id="([^"]+)"', content))
            for heading in re.findall(r'^#{1,6}\s+(.+)$', content, re.M):
                anchors.add(re.sub(r'[^\w\- ]', '', heading.lower()).replace(' ', '-'))
            if anchor not in anchors:
                errors.append(f'{file.relative_to(root)}: missing anchor {target}')
source = root / 'docs/product/novel-generation-product-requirements.md'
rendered = source.with_suffix('.html').read_text(encoding='utf-8')
expected = hashlib.sha256(source.read_text(encoding='utf-8-sig').encode('utf-8')).hexdigest()
if f'name="source-sha256" content="{expected}"' not in rendered:
    errors.append('产品 HTML 未与 Markdown 同步，请运行 python docs/product/render-product-html.py')
if errors:
    raise SystemExit('\n'.join(errors))
print(f'Markdown local links passed: {count}')
print('Product Markdown/HTML source hash passed')

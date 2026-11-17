#!/usr/bin/env python3
"""Compone dist/ desde src/ (partials + pages). Sin dependencias. Uso: python3 build.py [--base https://dominio]"""
import re, sys, pathlib, datetime
raiz = pathlib.Path(__file__).parent; src = raiz / 'src'; dist = raiz / 'dist'
base = next((sys.argv[i + 1] for i, a in enumerate(sys.argv) if a == '--base'), 'https://dominio-por-definir.co').rstrip('/')
MENU = [('/funcionalidades.html', 'Funcionalidades'), ('/asistente-ia.html', 'Asistente IA'), ('/integracion.html', 'Integración'), ('/planes.html', 'Planes'), ('/seguridad.html', 'Seguridad'), ('/recursos.html', 'Recursos')]
head, foot = (src / 'partials/head.html').read_text(), (src / 'partials/foot.html').read_text()
urls = []
for f in sorted((src / 'pages').rglob('*.html')):
    t = f.read_text(); m = re.match(r'<!--(.*?)-->\s*', t, re.S); meta = dict(l.split(':', 1) for l in m.group(1).strip().splitlines()); body = t[m.end():]
    path = meta['path'].strip(); menu = ''.join(f'<a href="{h}"{" aria-current=page" if h == path else ""}>{n}</a>' for h, n in MENU)
    h = head.replace('{{title}}', meta['title'].strip()).replace('{{desc}}', meta['desc'].strip()).replace('{{base}}', base).replace('{{path}}', path).replace('{{menu}}', menu).replace('{{extra_head}}', '')
    out = dist / f.relative_to(src / 'pages'); out.parent.mkdir(parents=True, exist_ok=True); out.write_text(h + body + foot)
    if path != '/404.html': urls.append(path)
hoy = datetime.date.today().isoformat()
(dist / 'sitemap.xml').write_text('<?xml version="1.0" encoding="UTF-8"?>\n<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">\n' + ''.join(f'<url><loc>{base}{u}</loc><lastmod>{hoy}</lastmod></url>\n' for u in urls) + '</urlset>\n')
(dist / 'robots.txt').write_text(f'User-agent: *\nAllow: /\nSitemap: {base}/sitemap.xml\n')
print(f'{len(urls)} páginas → dist/')

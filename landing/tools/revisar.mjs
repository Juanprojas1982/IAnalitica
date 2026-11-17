// Revisión: capturas por ancho, enlaces internos rotos, imágenes sin alt, desbordamiento horizontal. Uso: node tools/revisar.mjs
import { chromium } from 'playwright'; import { mkdirSync } from 'node:fs';
const B = 'http://localhost:8099', paginas = ['/', '/funcionalidades.html', '/asistente-ia.html', '/integracion.html', '/planes.html', '/seguridad.html', '/recursos.html', '/demo.html', '/casos/saludips.html', '/legal/privacidad.html', '/legal/terminos.html', '/404.html'];
mkdirSync('tools/.tmp/rev', { recursive: true }); const b = await chromium.launch(); let problemas = 0;
const vistos = new Set();
for (const [ancho, alto, n] of [[1440, 900, 'pc'], [390, 844, 'movil']]) {
  const ctx = await b.newContext({ viewport: { width: ancho, height: alto } }); const p = await ctx.newPage();
  const errs = []; p.on('console', m => m.type() === 'error' && errs.push(m.text())); p.on('pageerror', e => errs.push(String(e)));
  for (const u of paginas) {
    await p.goto(B + u); await p.waitForTimeout(300);
    await p.evaluate(() => document.querySelectorAll('.rev').forEach(e => e.classList.add('v')));
    const r = await p.evaluate(() => ({ sw: document.documentElement.scrollWidth, cw: document.documentElement.clientWidth, sinAlt: [...document.images].filter(i => !i.hasAttribute('alt')).length, links: [...document.querySelectorAll('a[href^="/"]')].map(a => a.getAttribute('href').split('#')[0]), h1: document.querySelectorAll('h1').length, icTexto: [...document.querySelectorAll('.ic')].filter(e => e.textContent.trim() || !e.querySelector('svg')).length }));
    if (r.sw > r.cw + 1) { console.log(`✗ ${n} ${u}: desborda (${r.sw}>${r.cw})`); problemas++; }
    if (r.sinAlt) { console.log(`✗ ${u}: ${r.sinAlt} imagen(es) sin alt`); problemas++; }
    if (r.icTexto) { console.log(`✗ ${u}: ${r.icTexto} icono(s) .ic sin SVG o con texto`); problemas++; }
    if (r.h1 !== 1) { console.log(`✗ ${u}: ${r.h1} h1`); problemas++; }
    r.links.forEach(l => vistos.add(l));
    if (n === 'pc' || u === '/') await p.screenshot({ path: `tools/.tmp/rev/${n}-${u.replace(/[\/.]/g, '_') || 'inicio'}.png`, fullPage: true });
  }
  if (errs.length) { console.log(`✗ ${n} errores de consola:`, [...new Set(errs)].slice(0, 5)); problemas++; }
  await ctx.close();
}
// LD-01: en la primera visita el banner de cookies aparece y no tapa los botones del hero
for (const [ancho, alto] of [[1440, 900], [1366, 768], [390, 844], [360, 740]]) {
  const ctx = await b.newContext({ viewport: { width: ancho, height: alto } }); const p = await ctx.newPage();
  await p.goto(B + '/'); await p.waitForTimeout(300);
  const r = await p.evaluate(() => {
    const ck = document.querySelector('#cookies.on'); if (!ck) return { visible: false };
    const c = ck.getBoundingClientRect();
    const tapa = [...document.querySelectorAll('.hero .cta .btn')].some(e => { const x = e.getBoundingClientRect(); return x.left < c.right && x.right > c.left && x.top < c.bottom && x.bottom > c.top; });
    return { visible: true, tapa, alto: Math.round(c.height) };
  });
  if (!r.visible) { console.log(`✗ ${ancho}×${alto}: el banner de cookies no aparece en la primera visita`); problemas++; }
  else if (r.tapa) { console.log(`✗ ${ancho}×${alto}: el banner de cookies tapa los botones del hero`); problemas++; }
  else if (ancho <= 640 && r.alto > 120) { console.log(`✗ ${ancho}×${alto}: el banner de cookies mide ${r.alto} px (máximo 120 en móvil)`); problemas++; }
  await ctx.close();
}
// LD-03: en móvil, imágenes compactas y ningún video; en escritorio, el video se activa
{ const ctx = await b.newContext({ viewport: { width: 390, height: 844 } }); const p = await ctx.newPage(); const videos = [];
  p.on('request', q => /\.(webm|mp4)(\?|$)/.test(q.url()) && videos.push(q.url()));
  for (const u of ['/', '/asistente-ia.html']) { await p.goto(B + u); await p.evaluate(() => window.scrollTo(0, document.body.scrollHeight)); await p.waitForTimeout(800); }
  if (videos.length) { console.log('✗ móvil descarga video:', videos); problemas++; }
  await p.goto(B + '/'); await p.locator('#asistente .shot img').scrollIntoViewIfNeeded(); await p.waitForTimeout(600);
  const m = await p.evaluate(() => ['.hero .marco img', '#asistente .shot img'].map(s => { const i = document.querySelector(s); return { s, w: Math.round(i.getBoundingClientRect().width), src: i.currentSrc }; }));
  for (const x of m) if (x.w < 330 || !/-c(-2x)?\.webp$/.test(x.src)) { console.log('✗ móvil, medio no compacto o angosto:', x); problemas++; }
  await ctx.close(); }
{ const ctx = await b.newContext({ viewport: { width: 1440, height: 900 } }); const p = await ctx.newPage();
  await p.goto(B + '/'); await p.locator('#asistente').scrollIntoViewIfNeeded(); await p.waitForTimeout(1500);
  const ok = await p.evaluate(() => [...document.querySelectorAll('video[data-video]')].every(v => !v.hidden && v.currentSrc));
  if (!ok) { console.log('✗ escritorio: algún video no se activó'); problemas++; }
  await ctx.close(); }
const p = await (await b.newContext()).newPage();
for (const l of vistos) { if (!l) continue; const r = await p.request.get(B + l); if (!r.ok()) { console.log(`✗ enlace roto: ${l} (${r.status()})`); problemas++; } }
await b.close(); console.log(problemas ? `${problemas} problema(s)` : 'Sin problemas');

// Genera los medios del sitio desde tools/escena.html: capturas WebP (amplias y compactas) y videos (WebM y MP4). Requiere ffmpeg. Uso: node tools/render.mjs
import { chromium } from 'playwright'; import { mkdirSync, renameSync } from 'node:fs'; import { execSync } from 'node:child_process'; import { pathToFileURL } from 'node:url'; import path from 'node:path';
const raiz = path.resolve(import.meta.dirname, '..'), url = q => pathToFileURL(path.join(raiz, 'tools/escena.html')).href + '?' + q;
const out = path.join(raiz, 'dist/assets'); mkdirSync(out, { recursive: true }); const tmp = path.join(raiz, 'tools/.tmp'); mkdirSync(tmp, { recursive: true });
const AMPLIA = { width: 1280, height: 720 }, COMPACTA = { width: 720, height: 540 };
const b = await chromium.launch();
// Capturas: [vista, compacta, escala, archivo]
for (const [v, c, dsf, nombre] of [['gerencial', false, 1, 'dash-gerencial'], ['cartera', false, 1, 'dash-cartera'], ['ciclo', false, 1, 'dash-ciclo'],
  ['gerencial', true, 1, 'dash-gerencial-c'], ['gerencial', true, 2, 'dash-gerencial-c-2x'], ['asistente', true, 1, 'dash-asistente-c'], ['asistente', true, 2, 'dash-asistente-c-2x']]) {
  const ctx = await b.newContext({ viewport: c ? COMPACTA : AMPLIA, deviceScaleFactor: dsf }); const p = await ctx.newPage();
  await p.goto(url(`v=${v}&t=end${c ? '&c=1' : ''}`)); await p.waitForTimeout(400);
  const png = path.join(tmp, `${nombre}.png`); await p.screenshot({ path: png }); await ctx.close();
  execSync(`ffmpeg -y -loglevel error -i ${png} -c:v libwebp -quality 80 -compression_level 6 ${out}/${nombre}.webp`);
}
// Videos (12 s en bucle): [vista, compacta, archivo]
for (const [v, c, nombre] of [['gerencial', false, 'hero-gerencial'], ['asistente', true, 'hero-asistente-c']]) {
  const size = c ? COMPACTA : AMPLIA, dur = 12000;
  const ctx = await b.newContext({ viewport: size, recordVideo: { dir: tmp, size } }); const p = await ctx.newPage();
  await p.goto(url(`v=${v}&dur=${dur}${c ? '&c=1' : ''}`)); await p.waitForTimeout(dur + 300);
  const f = await p.video().path(); await ctx.close(); const i = path.join(tmp, `video-${nombre}.webm`); renameSync(f, i);
  execSync(`ffmpeg -y -loglevel error -ss 0.4 -i ${i} -an -c:v libvpx-vp9 -b:v 0 -crf 38 -row-mt 1 ${out}/${nombre}.webm`);
  execSync(`ffmpeg -y -loglevel error -ss 0.4 -i ${i} -an -c:v libx264 -crf 29 -preset slow -pix_fmt yuv420p -movflags +faststart ${out}/${nombre}.mp4`);
}
await b.close(); console.log('Medios generados en dist/assets');

// Genera los medios del sitio desde tools/escena.html: video (webm→mp4), pósters y capturas. Uso: node tools/render.mjs
import { chromium } from 'playwright'; import { mkdirSync, renameSync, readdirSync } from 'node:fs'; import { execSync } from 'node:child_process'; import { pathToFileURL } from 'node:url'; import path from 'node:path';
const raiz = path.resolve(import.meta.dirname, '..'), url = v => pathToFileURL(path.join(raiz, 'tools/escena.html')).href + '?' + v;
const out = path.join(raiz, 'dist/assets'); mkdirSync(out, { recursive: true }); const tmp = path.join(raiz, 'tools/.tmp'); mkdirSync(tmp, { recursive: true });
const b = await chromium.launch();
// Capturas finales
const ctx = await b.newContext({ viewport: { width: 1280, height: 720 }, deviceScaleFactor: 1 });
for (const v of ['gerencial', 'cartera', 'ciclo', 'asistente', 'grupo']) { const p = await ctx.newPage(); await p.goto(url(`v=${v}&t=end`)); await p.waitForTimeout(400); await p.screenshot({ path: path.join(tmp, `dash-${v}.png`) }); await p.close(); }
await ctx.close();
// Videos (grabación de Playwright: 12 s por vista, en bucle)
for (const [v, dur] of [['gerencial', 12000], ['asistente', 12000]]) {
  const c = await b.newContext({ viewport: { width: 1280, height: 720 }, recordVideo: { dir: tmp, size: { width: 1280, height: 720 } } });
  const p = await c.newPage(); await p.goto(url(`v=${v}&dur=${dur}`)); await p.waitForTimeout(dur + 300); const f = await p.video().path(); await c.close(); renameSync(f, path.join(tmp, `video-${v}.webm`));
}
await b.close();
for (const v of ['gerencial', 'asistente']) {
  const i = path.join(tmp, `video-${v}.webm`);
  execSync(`ffmpeg -y -loglevel error -ss 0.4 -i ${i} -an -c:v libvpx-vp9 -b:v 0 -crf 38 -row-mt 1 ${out}/hero-${v}.webm`);
  execSync(`ffmpeg -y -loglevel error -ss 0.4 -i ${i} -an -c:v libx264 -crf 29 -preset slow -pix_fmt yuv420p -movflags +faststart ${out}/hero-${v}.mp4`);
}
console.log(readdirSync(tmp).join('\n'));

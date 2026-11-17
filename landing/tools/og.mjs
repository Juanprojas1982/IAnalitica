// Genera dist/assets/og.png (1200×630) desde tools/og.html. Uso: node tools/og.mjs (después de render.mjs)
import { chromium } from 'playwright'; import { pathToFileURL } from 'node:url'; import path from 'node:path';
const raiz = path.resolve(import.meta.dirname, '..'); const b = await chromium.launch();
const p = await b.newPage({ viewport: { width: 1200, height: 630 }, deviceScaleFactor: 1 });
await p.goto(pathToFileURL(path.join(raiz, 'tools/og.html')).href); await p.waitForTimeout(500);
await p.screenshot({ path: path.join(raiz, 'dist/assets/og.png') }); await b.close(); console.log('og.png');

import { createServer } from 'node:http';
import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import { extname, join, resolve } from 'node:path';
import { chromium } from 'playwright';

const [wwwrootArg, baseUrlArg] = process.argv.slice(2);

if (!wwwrootArg || !baseUrlArg) {
    console.error('usage: prerender.mjs <publish-wwwroot> <base-url>');
    console.error('  e.g. prerender.mjs publish/wwwroot https://lisihasaj.github.io/Deja/');
    process.exit(1);
}

const wwwroot = resolve(wwwrootArg);
const baseUrl = baseUrlArg.endsWith('/') ? baseUrlArg : baseUrlArg + '/';
const basePath = new URL(baseUrl).pathname;

const MIME = {
    '.html': 'text/html; charset=utf-8',
    '.js': 'text/javascript',
    '.mjs': 'text/javascript',
    '.css': 'text/css',
    '.json': 'application/json',
    '.wasm': 'application/wasm',
    '.svg': 'image/svg+xml',
    '.png': 'image/png',
    '.webmanifest': 'application/manifest+json',
    '.dat': 'application/octet-stream',
    '.blat': 'application/octet-stream',
    '.br': 'application/octet-stream',
    '.gz': 'application/octet-stream',
    '.xml': 'application/xml',
    '.txt': 'text/plain; charset=utf-8',
};

// Mirrors GitHub Pages: unknown paths fall back to the SPA shell rather than a bare 404.
const server = createServer(async (req, res) => {
    const path = decodeURIComponent(new URL(req.url, 'http://localhost').pathname);
    const relative = path.startsWith(basePath) ? path.slice(basePath.length) : path.replace(/^\/+/, '');

    for (const candidate of [join(wwwroot, relative), join(wwwroot, relative, 'index.html')]) {
        if (existsSync(candidate) && !candidate.endsWith('/')) {
            try {
                const body = await readFile(candidate);
                res.writeHead(200, { 'content-type': MIME[extname(candidate)] ?? 'application/octet-stream' });
                res.end(body);
                return;
            } catch {
                // fall through to the SPA shell
            }
        }
    }

    res.writeHead(200, { 'content-type': 'text/html; charset=utf-8' });
    res.end(await readFile(join(wwwroot, 'index.html')));
});

const port = await new Promise(ok => server.listen(0, '127.0.0.1', () => ok(server.address().port)));
const origin = `http://127.0.0.1:${port}${basePath}`;

const routes = JSON.parse(await readFile(join(wwwroot, 'prerender-routes.json'), 'utf8'));

const browser = await chromium.launch();
let written = 0;
let failed = 0;

async function render(page, href, lang) {
    await page.addInitScript(language => {
        if (language === 'de') localStorage.setItem('deja-docs-lang', 'de');
        else localStorage.removeItem('deja-docs-lang');
    }, lang);

    await page.goto(origin + href, { waitUntil: 'networkidle', timeout: 90_000 });
    await page.waitForFunction(
        () => {
            const app = document.querySelector('#app');
            return app && !app.querySelector('.boot-splash') && app.innerHTML.length > 500;
        },
        { timeout: 90_000 },
    );

    // Demos fetch live data on mount; let their first paint settle so the markup is not mid-load.
    await page.waitForTimeout(1200);

    return page.evaluate(() => document.querySelector('#app').innerHTML);
}

for (const { href, lang, file } of routes) {
    const target = join(wwwroot, file);
    const context = await browser.newContext();
    const page = await context.newPage();

    try {
        const markup = await render(page, href, lang);
        const shell = await readFile(target, 'utf8');

        if (!shell.includes('<!--prerender-->')) {
            throw new Error(`${file} has no <!--prerender--> marker`);
        }

        const hydrated = shell.replace(
            /<!--prerender-->[\s\S]*?<!--\/prerender-->/,
            () => `<!--prerender-->${markup}<!--/prerender-->`,
        );

        await mkdir(join(target, '..'), { recursive: true });
        await writeFile(target, hydrated);
        console.log(`${file.padEnd(46)}← ${(markup.length / 1024).toFixed(0)} KB [${lang}]`);
        written++;
    } catch (error) {
        console.error(`FAILED ${file} [${lang}]: ${error.message}`);
        failed++;
    } finally {
        await context.close();
    }
}

await browser.close();
server.close();

console.log(`\nprerendered ${written}/${routes.length} routes`);

if (failed > 0) {
    console.error(`${failed} route(s) failed to prerender`);
    process.exit(1);
}

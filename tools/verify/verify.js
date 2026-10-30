// End-to-end check of the PoC with Playwright against the local LD stub.
// Usage: node verify.js <mode: stub|bootstrap> [baseUrl] [stubUrl] [screenshotDir]
const { chromium } = require('playwright');
const mode = process.argv[2] || 'stub';
const base = process.argv[3] || 'http://localhost:5080';
const stub = process.argv[4] || 'http://localhost:5199';
const shots = process.argv[5] || '.';

const results = [];
const check = (name, ok, detail) => { results.push({ name, ok: !!ok, detail }); console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail !== undefined ? '  — ' + JSON.stringify(detail) : ''}`); };
const stubLog = async () => (await fetch(`${stub}/_stub/log`)).json();
const text = (page, sel) => page.locator(sel).innerText();

(async () => {
  await fetch(`${stub}/_stub/reset`, { method: 'POST' });
  const browser = await chromium.launch();
  const page = await browser.newPage({ viewport: { width: 900, height: 900 } });
  const consoleErrors = [];
  page.on('console', m => { if (m.type() === 'error') consoleErrors.push(m.text()); });
  page.on('pageerror', e => consoleErrors.push(String(e)));
  // Record every value the banner ever shows (to see bootstrap -> stream reconciliation).
  await page.addInitScript(() => {
    window.__banner = [];
    new MutationObserver(() => {
      const el = document.getElementById('flag-banner');
      if (el && window.__banner[window.__banner.length - 1] !== el.innerText) window.__banner.push(el.innerText);
    }).observe(document, { subtree: true, childList: true, characterData: true });
  });

  const t0 = Date.now();
  await page.goto(base);
  await page.waitForSelector('#flag-banner', { timeout: 30000 });
  check('app rendered after LD init gate', true, { ms: Date.now() - t0, initStatus: await text(page, '#init-status') });

  if (mode === 'stub') {
    check('initial string flag from eval endpoint', (await text(page, '#flag-banner')).includes('stub (initial)'), await text(page, '#flag-banner'));
    check('int flag (anonymous context => 4)', (await text(page, '#flag-max-items')) === '4', await text(page, '#flag-max-items'));
    check('JSON flag deserialized', (await text(page, '#flag-theme')).includes('#3b82f6'), await text(page, '#flag-theme'));
    check('<FeatureFlag> off for anonymous', (await text(page, '#flag-checkout')).includes('Classic'), await text(page, '#flag-checkout'));
    let log = await stubLog();
    const firstEval = log.requests.find(r => r.path.startsWith('/sdk/evalx/'));
    check('SDK polled eval endpoint with anonymous generated key', firstEval && firstEval.contextKey, firstEval?.contextKey);
    await page.waitForFunction(async () => true);
    for (let i = 0; i < 20 && (await stubLog()).openStreams < 1; i++) await page.waitForTimeout(250);
    log = await stubLog();
    check('SSE stream connection open', log.openStreams >= 1, { openStreams: log.openStreams });
    await page.screenshot({ path: `${shots}/01-before-stream-change.png`, fullPage: true });

    const pushed = await (await fetch(`${stub}/_stub/patch?key=demo-banner&value=${encodeURIComponent('Updated live via the stream!')}`, { method: 'POST' })).json();
    const tPatch = Date.now();
    await page.waitForFunction(() => document.getElementById('flag-banner')?.innerText.includes('Updated live'), null, { timeout: 10000 });
    check('streamed patch re-rendered via C# FlagChanged event', true, { pushedTo: pushed.pushedTo, ms: Date.now() - tPatch, changeCount: await text(page, '#change-count'), lastChanged: await text(page, '#last-changed') });
    await page.screenshot({ path: `${shots}/02-after-stream-change.png`, fullPage: true });

    await page.click('#btn-alice');
    await page.waitForFunction(() => document.getElementById('identify-status')?.innerText.startsWith('completed'), null, { timeout: 10000 });
    check('identify(Alice) re-evaluated: <FeatureFlag> on', (await text(page, '#flag-checkout')).includes('ON'), await text(page, '#flag-checkout'));
    check('identify(Alice) re-evaluated: max-items 10', (await text(page, '#flag-max-items')) === '10', await text(page, '#flag-max-items'));
    log = await stubLog();
    { const kinds = log.requests.filter(r => r.contextKey === 'user-123').map(r => r.path.split('/')[1]); check('stub saw eval + stream for user-123', kinds.includes('sdk') && kinds.includes('eval'), kinds); }

    await page.click('#btn-track');
    await page.waitForSelector('#track-status', { timeout: 10000 });
    check('track + flush returned', (await text(page, '#track-status')).includes('flush result=True'), await text(page, '#track-status'));
    log = await stubLog();
    const all = log.events.filter(e => e.kind === 'bulk').flatMap(e => e.payload);
    const custom = all.find(e => e.kind === 'custom' && e.key === 'checkout-clicked');
    check('custom event received by stub', custom, custom && { key: custom.key, data: custom.data, metricValue: custom.metricValue, context: custom.context });
    check('event kinds received', true, [...new Set(all.map(e => e.kind))]);

    await page.click('#btn-bob');
    await page.waitForFunction(() => document.getElementById('flag-max-items')?.innerText === '5', null, { timeout: 10000 });
    check('identify(Bob) re-evaluated: max-items 5, checkout off', (await text(page, '#flag-checkout')).includes('Classic'), await text(page, '#flag-max-items'));

    // Tab-close delivery: flushInterval is 30 s, so a batch arriving right after navigating away came from the pagehide/visibility flush.
    const before = (await stubLog()).events.filter(e => e.kind === 'bulk').length;
    await page.goto('about:blank');
    await new Promise(r => setTimeout(r, 1500));
    const afterLog = await stubLog();
    const after = afterLog.events.filter(e => e.kind === 'bulk');
    const last = after[after.length - 1];
    check('events flushed on pagehide (navigate away)', after.length > before, { batchesBefore: before, batchesAfter: after.length, lastBatchKinds: last && [...new Set(last.payload.map(e => e.kind))] });
  } else {
    const history = await page.evaluate(() => window.__banner);
    check('first rendered banner came from .NET server SDK bootstrap', history[0]?.includes('bootstrap'), history);
    check('bootstrapped context is server-decided user-123: <FeatureFlag> ON on first render', (await text(page, '#flag-checkout')).includes('ON'), await text(page, '#flag-checkout'));
    for (let i = 0; i < 20 && (await stubLog()).openStreams < 1; i++) await page.waitForTimeout(250);
    await page.waitForTimeout(1000);
    const log = await stubLog();
    check('no polling eval request (bootstrap used instead)', !log.requests.some(r => r.path.startsWith('/sdk/evalx/')), log.requests.map(r => r.path.split('/').slice(0, 3).join('/')));
    const s = log.requests.find(r => r.path.startsWith('/eval/'));
    check('stream opened for user-123 with secure-mode hash h=..., verified by stub', s && s.contextKey === 'user-123' && s.hashValid === true, s && { contextKey: s.contextKey, h: s.h?.slice(0, 12) + '…', hashValid: s.hashValid });
    const history2 = await page.evaluate(() => window.__banner);
    check('stream put then reconciled values (banner history)', true, history2);
    await page.screenshot({ path: `${shots}/03-bootstrap-mode.png`, fullPage: true });
  }
  check('no console errors', consoleErrors.length === 0, consoleErrors.slice(0, 5));
  await browser.close();
  const failed = results.filter(r => !r.ok).length;
  console.log(`\n${results.length - failed}/${results.length} checks passed (${mode})`);
  process.exit(failed ? 1 : 0);
})().catch(e => { console.error(e); process.exit(2); });

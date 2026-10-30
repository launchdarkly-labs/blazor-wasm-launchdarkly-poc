// Minimal local stand-in for LaunchDarkly's client-side endpoints, for testing without a real environment.
// Serves: polling eval (FDv1), SSE stream (put/patch), goals, and accepts analytics/diagnostic events.
// Control: POST /_stub/patch?key=demo-banner&value=... pushes a patch to open streams; GET /_stub/log dumps traffic.
// Usage: node stub.js [port]   (env STUB_SDK_KEY to verify secure-mode hashes, STUB_AUTO_PATCH_MS to auto-patch)
const http = require('http');
const crypto = require('crypto');

const port = Number(process.argv[2] || process.env.PORT || 5199);
const sdkKey = process.env.STUB_SDK_KEY || 'sdk-local-test-key';
const autoPatchMs = Number(process.env.STUB_AUTO_PATCH_MS || 0);

const state = { banner: 'Hello from the LaunchDarkly stub (initial)', bannerVersion: 1 };
const streams = new Set();
const log = { requests: [], events: [] };

function decodeContext(b64) {
  try { return JSON.parse(Buffer.from(b64.replace(/-/g, '+').replace(/_/g, '/'), 'base64').toString('utf8')); }
  catch { return null; }
}
function contextKey(ctx) {
  if (!ctx) return null;
  if (ctx.kind === 'multi') return ctx.user?.key ?? null;
  return ctx.key ?? null;
}
function flagsFor(ctx) {
  const key = contextKey(ctx);
  const alice = key === 'user-123';
  return {
    'demo-banner': { value: state.banner, version: state.bannerVersion, variation: state.bannerVersion - 1, trackEvents: false },
    'new-checkout': { value: alice, version: 1, variation: alice ? 0 : 1, trackEvents: true },
    'max-items': { value: alice ? 10 : key === 'user-456' ? 5 : 4, version: 1, variation: 0, trackEvents: false },
    'theme-config': { value: { color: alice ? '#0b7a75' : '#3b82f6', rounded: alice }, version: 1, variation: 0, trackEvents: false },
  };
}
function verifyHash(ctx, h) {
  if (!h) return null;
  const expected = crypto.createHmac('sha256', sdkKey).update(String(contextKey(ctx))).digest('hex');
  return expected === h;
}
function cors(res) {
  res.setHeader('Access-Control-Allow-Origin', '*');
  res.setHeader('Access-Control-Allow-Methods', 'GET,POST,REPORT,OPTIONS');
  res.setHeader('Access-Control-Allow-Headers', '*');
  res.setHeader('Access-Control-Expose-Headers', 'Date');
}
function json(res, status, body) { cors(res); res.writeHead(status, { 'content-type': 'application/json' }); res.end(JSON.stringify(body)); }

function pushPatch(key, value) {
  if (key === 'demo-banner') { state.banner = value; state.bannerVersion++; }
  const data = JSON.stringify({ key, version: state.bannerVersion, value, variation: state.bannerVersion - 1, trackEvents: false });
  for (const s of streams) s.write(`event: patch\ndata: ${data}\n\n`);
  return streams.size;
}

const server = http.createServer((req, res) => {
  const url = new URL(req.url, `http://localhost:${port}`);
  const parts = url.pathname.split('/').filter(Boolean);
  if (req.method === 'OPTIONS') { cors(res); res.writeHead(204); return res.end(); }

  let body = '';
  req.on('data', c => (body += c));
  req.on('end', () => {
    const entry = { t: new Date().toISOString(), method: req.method, path: url.pathname, h: url.searchParams.get('h') };
    if (!url.pathname.startsWith('/_stub')) log.requests.push(entry);

    // GET /sdk/evalx/{env}/contexts/{b64}
    if (req.method === 'GET' && parts[0] === 'sdk' && parts[1] === 'evalx' && parts[3] === 'contexts') {
      const ctx = decodeContext(parts[4]); entry.contextKey = contextKey(ctx); entry.hashValid = verifyHash(ctx, entry.h);
      return json(res, 200, flagsFor(ctx));
    }
    if (req.method === 'GET' && parts[0] === 'sdk' && parts[1] === 'goals') return json(res, 200, []);
    // .NET client (mobile) SDK endpoints, used only by repro/DotnetClientSdkRepro
    if (req.method === 'GET' && parts[0] === 'msdk' && parts[1] === 'evalx' && parts[2] === 'contexts') {
      const ctx = decodeContext(parts[3]); entry.contextKey = contextKey(ctx);
      return json(res, 200, flagsFor(ctx));
    }

    // GET /eval/{env}/{b64}  -> SSE
    if (req.method === 'GET' && ((parts[0] === 'eval' && parts.length >= 3) || parts[0] === 'meval')) {
      const ctx = decodeContext(parts[0] === 'meval' ? parts[1] : parts[2]); entry.contextKey = contextKey(ctx); entry.hashValid = verifyHash(ctx, entry.h);
      cors(res);
      res.writeHead(200, { 'content-type': 'text/event-stream', 'cache-control': 'no-cache', connection: 'keep-alive' });
      res.write(`event: put\ndata: ${JSON.stringify(flagsFor(ctx))}\n\n`);
      streams.add(res);
      const ping = setInterval(() => res.write(':\n\n'), 15000);
      res.on('close', () => { clearInterval(ping); streams.delete(res); });
      if (autoPatchMs) setTimeout(() => pushPatch('demo-banner', 'Updated live via the stream!'), autoPatchMs);
      return;
    }

    // POST /events/bulk/{env} and /events/diagnostic/{env}
    if (req.method === 'POST' && (parts[0] === 'events' || parts[0] === 'mobile')) {
      let payload; try { payload = JSON.parse(body); } catch { payload = body; }
      log.events.push({ t: entry.t, kind: parts[1], headers: { schema: req.headers['x-launchdarkly-event-schema'], payloadId: req.headers['x-launchdarkly-payload-id'] }, payload });
      cors(res); res.writeHead(202); return res.end();
    }

    if (url.pathname === '/_stub/patch' && req.method === 'POST') {
      const n = pushPatch(url.searchParams.get('key') || 'demo-banner', url.searchParams.get('value') || 'Updated live via the stream!');
      return json(res, 200, { pushedTo: n });
    }
    if (url.pathname === '/_stub/log') return json(res, 200, { ...log, openStreams: streams.size });
    if (url.pathname === '/_stub/reset' && req.method === 'POST') { log.requests = []; log.events = []; state.banner = 'Hello from the LaunchDarkly stub (initial)'; state.bannerVersion = 1; return json(res, 200, {}); }

    json(res, 404, { error: 'not found', path: url.pathname });
  });
});
server.listen(port, () => console.log(`LD stub listening on http://localhost:${port}`));

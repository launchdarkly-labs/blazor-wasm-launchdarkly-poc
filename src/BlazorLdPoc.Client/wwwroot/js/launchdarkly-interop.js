// Thin ES-module bridge between Blazor (C#) and the LaunchDarkly client-side JavaScript SDK.
// Loaded from C# with: IJSRuntime.InvokeAsync<IJSObjectReference>("import", "./js/launchdarkly-interop.js")
//
// Package: @launchdarkly/js-client-sdk 4.13.0 (vendored, self-contained ESM build in wwwroot/lib/launchdarkly).
// Everything that crosses the boundary is plain JSON (System.Text.Json on the C# side).

const DEFAULT_SDK_URL = '../lib/launchdarkly/js-client-sdk-4.13.0.js';

let ld = null;          // the imported SDK module
let client = null;      // LDClient
let dotNet = null;      // DotNetObjectReference<LaunchDarklyClient>
let removeLifecycleHooks = null;
const perFlagHandlers = new Map(); // flagKey -> handler registered with client.on(`change:${key}`)

function requireClient() {
  if (!client) throw new Error('LaunchDarkly client is not initialized. Call initialize() first.');
  return client;
}

function onChange(context, changedKeys) {
  // v4 'change' signature: (context, string[] changedKeys). Values are re-read on the C# side.
  dotNet?.invokeMethodAsync('OnFlagsChanged', changedKeys ?? []).catch(err => console.warn('[ld-interop] OnFlagsChanged failed', err));
}

function onError(context, err) {
  dotNet?.invokeMethodAsync('OnError', String(err?.message ?? err)).catch(() => { });
}

// The v4 SDK already flushes on visibilitychange/pagehide (automaticBackgroundHandling, default true).
// This hook mirrors that so the wrapper behaves the same with v3 or automaticBackgroundHandling:false.
// Events are POSTed with fetch keepalive:true by the SDK, which is what lets them survive unload.
function installFlushOnHide() {
  const flush = () => { client?.flush().catch(() => { }); };
  const onVisibility = () => { if (document.visibilityState === 'hidden') flush(); };
  window.addEventListener('pagehide', flush);
  document.addEventListener('visibilitychange', onVisibility);
  return () => {
    window.removeEventListener('pagehide', flush);
    document.removeEventListener('visibilitychange', onVisibility);
  };
}

/**
 * @param {string} clientSideId  LaunchDarkly client-side ID (not the SDK key).
 * @param {object} context       LDContext as JSON.
 * @param {object} options       LDOptions as JSON (baseUri, streamUri, eventsUri, streaming, ...). Optional extra:
 *                               logLevel ('debug'|'info'|'warn'|'error'|'none') -> basicLogger.
 * @param {object} dotNetRef     DotNetObjectReference for callbacks.
 * @param {object} init          { timeoutSeconds, bootstrap, hash, sdkUrl }
 * @returns {{status: 'complete'|'failed'|'timeout', error: string|null}}
 */
export async function initialize(clientSideId, context, options, dotNetRef, init) {
  if (client) await close();
  init = init ?? {};
  ld = ld ?? await import(init.sdkUrl || DEFAULT_SDK_URL);
  dotNet = dotNetRef;

  const opts = { ...(options ?? {}) };
  if (opts.logLevel) {
    // basicLogger writes everything to console.error by default; route each level to its console method.
    opts.logger = ld.basicLogger({
      level: opts.logLevel,
      destination: { debug: console.debug, info: console.info, warn: console.warn, error: console.error },
    });
  }
  delete opts.logLevel;

  client = ld.createClient(clientSideId, context, opts);
  // Register listeners before start() so nothing is missed. Subscribing to 'change' also makes
  // the SDK open its streaming connection (unless options.streaming === false).
  client.on('change', onChange);
  client.on('error', onError);

  client.start({
    bootstrap: init.bootstrap ?? undefined,
    identifyOptions: init.hash ? { hash: init.hash } : undefined,
  });
  const result = await client.waitForInitialization({ timeout: init.timeoutSeconds ?? 5 });

  removeLifecycleHooks = installFlushOnHide();
  return { status: result.status, error: result.error ? String(result.error.message ?? result.error) : null };
}

export function variation(key, defaultValue) { return requireClient().variation(key, defaultValue); }
export function variationDetail(key, defaultValue) { return requireClient().variationDetail(key, defaultValue); }
// Typed variants: on a type mismatch the SDK returns the default and reports a WRONG_TYPE reason.
export function boolVariation(key, defaultValue) { return requireClient().boolVariation(key, defaultValue); }
export function stringVariation(key, defaultValue) { return requireClient().stringVariation(key, defaultValue); }
export function numberVariation(key, defaultValue) { return requireClient().numberVariation(key, defaultValue); }
export function jsonVariation(key, defaultValue) { return requireClient().jsonVariation(key, defaultValue); }
export function allFlags() { return requireClient().allFlags(); }
export function getContext() { return requireClient().getContext() ?? null; }

/** Per-flag subscription: calls C# OnFlagChanged(key) when that flag changes. */
export function subscribe(key) {
  if (perFlagHandlers.has(key)) return;
  const handler = () => dotNet?.invokeMethodAsync('OnFlagChanged', key).catch(() => { });
  perFlagHandlers.set(key, handler);
  requireClient().on(`change:${key}`, handler);
}

export function unsubscribe(key) {
  const handler = perFlagHandlers.get(key);
  if (handler && client) client.off(`change:${key}`, handler);
  perFlagHandlers.delete(key);
}

/** @returns {{status: string, error: string|null}} status: completed | error | timeout | shed */
export async function identify(context, hash, bootstrap, timeoutSeconds) {
  const result = await requireClient().identify(context, {
    hash: hash ?? undefined,
    bootstrap: bootstrap ?? undefined,
    timeout: timeoutSeconds ?? undefined,
    sheddable: false,
  });
  return { status: result.status, error: result.error ? String(result.error.message ?? result.error) : null };
}

export function track(key, data, metricValue) {
  requireClient().track(key, data ?? undefined, metricValue ?? undefined);
}

export async function flush() {
  const r = await requireClient().flush();
  return { result: !!r?.result, error: r?.error ? String(r.error.message ?? r.error) : null };
}

export async function close() {
  removeLifecycleHooks?.();
  removeLifecycleHooks = null;
  if (client) {
    for (const [key, handler] of perFlagHandlers) client.off(`change:${key}`, handler);
    perFlagHandlers.clear();
    client.off('change', onChange);
    client.off('error', onError);
    try { await client.flush(); } catch { /* ignore */ }
    await client.close();
  }
  client = null;
  dotNet = null;
}

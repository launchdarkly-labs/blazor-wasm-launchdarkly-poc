# Test report: LaunchDarkly in Blazor WebAssembly

**Date:** October 2026 · **How to run everything below:** see the [README](../README.md).

## Summary

| Approach | Result |
|---|---|
| **.NET client-side SDK** (`LaunchDarkly.ClientSdk` 5.10.2) in Blazor WebAssembly | ❌ **Not usable out of the box.** The default configuration throws. With workarounds, the best case is streaming flags with **events turned off** (no analytics, metrics, experiments or guarded rollouts), and synchronous `Identify()` fails. Tracked as [launchdarkly/dotnet-core#58](https://github.com/launchdarkly/dotnet-core/issues/58) (open, no timeline). |
| **JS SDK behind a C# facade** (`@launchdarkly/js-client-sdk` 4.13.0) | ✅ **Works.** Passed all 24 automated checks against a local stub, and all checks against a real LaunchDarkly environment: typed flags, live updates, identify and targeting, custom events, and event flushing on page close. |
| **.NET server SDK** (`LaunchDarkly.ServerSdk` 8.17.2) in the ASP.NET Core host | ✅ **Works.** Server-side evaluation matches the browser. Bootstrap gives a correct first render with a secure-mode hash, and flag changes reach the server live. |

## Setup

| Item | Version |
|---|---|
| .NET | Projects target `net8.0`; Blazor / ASP.NET Core packages 8.0.32. Tested with .NET SDK 10 (server via `DOTNET_ROLL_FORWARD=Major`). |
| Node.js / Playwright | Node 26; Playwright 1.64 with headless Chromium |
| LaunchDarkly | A dedicated test project, non-production environment. The local stub (`tools/ld-stub`) was used for Tests 1 and 2. |

**Flags** (all available to client-side SDKs): `demo-banner` (string), `max-items` (number), `theme-config` (JSON) and
`new-checkout` (boolean, with a rule: user `key` is one of `user-123` → `true`). Each flag's off value differed from the
code's fallback, so the page showed whether values really came from LaunchDarkly.

**Contexts:** Alice (`user-123`, `plan: gold`), Bob (`user-456`), and an anonymous user. In bootstrap mode, the server decides
the context (Alice).

---

## Test 1: .NET client SDK in Blazor WebAssembly

`repro/DotnetClientSdkRepro`, run against the local stub, one configuration per page load.

| # | Configuration | Result |
|---|---|---|
| A | Default | ❌ Throws `PlatformNotSupportedException` (HTTP handler decompression; IsolatedStorage) |
| B | Browser fetch handler, no persistence, streaming, events on | ❌ Hangs: the event processor fails ~20,000 times ("Cannot wait on monitors") and freezes the single WASM thread |
| C | Same as B, polling | ❌ Same hang as B |
| D | Polling, events off | ⚠️ Correct values, but no live updates and no events |
| E | Streaming, events off | ❌ Never initializes: the browser buffers the stream |
| F | E + fetch response streaming enabled per request | ⚠️ Initializes and streams; events still off |
| G | F, kept running | ✅ Live change and `IdentifyAsync` work · ❌ synchronous `Identify()` returns `false` |

## Test 2: PoC against the local stub (automated, no account)

`tools/verify/verify.js`. **Stub mode: 17/17 passed. Bootstrap mode: 7/7 passed.**

| Area | Verified |
|---|---|
| Startup | App renders only after LaunchDarkly initializes (~0.3 s); no flicker |
| Flag types | String, int, JSON (deserialized into a C# type) and `<FeatureFlag>` boolean |
| Live updates | Streamed change re-rendered the page through the C# `FlagChanged` event (~5 ms) |
| Identify | Alice → `new-checkout` on, `max-items` 10 · Bob → `max-items` 5, checkout off |
| Events | `checkout-clicked` received with data, `metricValue: 42.5` and context; `identify`, `feature`, `custom` and `summary` events received |
| Page close | Events flushed when the user navigates away |
| Bootstrap | First render used the server's values; no initial flag request from the browser; streaming opened with a secure-mode hash that the stub verified |
| Errors | No console errors in either mode |

## Test 3: PoC against real LaunchDarkly

| Check | Result |
|---|---|
| App initializes; string, number, JSON and boolean flags show LaunchDarkly's values (not code fallbacks) | ✅ |
| Flags fetched, streaming connection opened, events sent (`clientsdk` / `clientstream` / `events.launchdarkly.com`) | ✅ |
| Turning flags on in LaunchDarkly updates the open page without a reload | ✅ |
| Targeting rule applies: Identify Alice → "New checkout is ON"; Identify Bob → "Classic checkout" | ✅ |
| `checkout-clicked` appears in **Live events** with `metricValue: 42.5` | ✅ |
| Identified contexts appear in LaunchDarkly; no console warnings or errors | ✅ |

## Test 4: Server side against real LaunchDarkly

Server started with the environment's server-side SDK key (as an environment variable); client in bootstrap mode.

| Check | Result |
|---|---|
| `/api/launchdarkly/server-eval` returns real LaunchDarkly values for the server-decided context | ✅ |
| `/api/launchdarkly/bootstrap` contains only client-side flags, the `$flagsState` metadata and a secure-mode hash | ✅ |
| Bootstrap values match the server's own evaluation | ✅ |
| First render used the bootstrap values; no initial flag request from the browser | ✅ |
| Streaming connection opened with the same secure-mode hash | ✅ |
| A flag change reached both the page (no reload) and `server-eval` (no server restart) | ✅ |

---

## Not tested

| Item | Notes |
|---|---|
| **Multi-contexts** (for example `user` + `organization`) | All tests used single `user` contexts. Both SDKs support multi-contexts, and the C# facade passes contexts through unchanged, but organization targeting, bootstrap with a multi-context, and its secure-mode hash have not been verified. |
| Secure mode **enforced** in the environment | The hash is generated and sent, but secure mode was not turned on. With it on, the demo's Identify buttons would need a server-issued hash per user. |
| Standalone WASM hosting, CDN-loaded SDK | All tests used the ASP.NET Core host and the vendored SDK. |
| Browsers other than Chromium; load and long-running stability | Out of scope. |

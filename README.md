# Blazor WebAssembly + LaunchDarkly: proof of concept

The LaunchDarkly .NET client-side SDK (`LaunchDarkly.ClientSdk`) does not work in Blazor WebAssembly
([launchdarkly/dotnet-core#58](https://github.com/launchdarkly/dotnet-core/issues/58), open). This repo shows why, and
provides a working alternative: LaunchDarkly's **JavaScript browser SDK** (`@launchdarkly/js-client-sdk` 4.13.0) behind a
small **C# facade**, so Blazor code stays in C#. An optional ASP.NET Core server uses the LaunchDarkly **.NET server SDK**
to bootstrap the browser with pre-evaluated flags and a secure-mode hash.

## Contents

- [Documents](#documents)
- [What's in the repo](#whats-in-the-repo)
- [Prerequisites](#prerequisites)
- [Option 1: Run offline (no LaunchDarkly account)](#option-1-run-offline-no-launchdarkly-account)
- [Option 2: Run against your LaunchDarkly environment](#option-2-run-against-your-launchdarkly-environment)
- [Option 3: Add server bootstrap (recommended)](#option-3-add-server-bootstrap-recommended)
- [Optional: secure mode, metrics](#optional-secure-mode-metrics)
- [Troubleshooting](#troubleshooting)
- [Automated tests](#automated-tests-no-account-needed)
- [Reproduce the .NET client SDK failure](#reproduce-the-net-client-sdk-failure)
- [Configuration reference](#configuration-reference)
- [Cleanup](#cleanup)

## Documents

| Document | What it covers |
|---|---|
| [docs/DIAGRAMS.md](docs/DIAGRAMS.md) | Two diagrams: why the .NET client SDK fails, and the proposed design |
| [docs/before-after.html](docs/before-after.html) | Open in a browser. A plain Blazor app vs. this PoC, step by step, client and server, with phase diagrams |
| [docs/TEST-REPORT.md](docs/TEST-REPORT.md) | What was tested and the results |

## What's in the repo

```text
src/
├── BlazorLdPoc.Client/                  Blazor WebAssembly app
│   ├── LaunchDarkly/LaunchDarklyClient.cs   C# facade: typed flag reads, identify, track, change events
│   ├── Components/FeatureFlag.razor         <FeatureFlag Key="..."> re-renders when the flag changes
│   ├── Program.cs                           initializes LaunchDarkly before the first render
│   └── wwwroot/
│       ├── appsettings.json                 LaunchDarkly settings (client-side ID goes here)
│       ├── js/launchdarkly-interop.js       thin JS bridge; flushes events when the page closes
│       └── lib/launchdarkly/                vendored JS SDK (pinned 4.13.0)
└── BlazorLdPoc.Server/                  ASP.NET Core host + .NET server SDK
    ├── Program.cs                           /api/launchdarkly/bootstrap and /api/launchdarkly/server-eval
    └── FlagContextFactory.cs                decides the user (context) for each request
tools/
├── ld-stub/stub.js                      local fake LaunchDarkly (no account needed)
└── verify/verify.js                     Playwright end-to-end tests
repro/
├── DotnetClientSdkRepro/                reproduces the .NET client SDK failures (attempts A–G)
└── results/                             recorded logs of those attempts
docs/                                    diagrams, before/after walkthrough, test report
```

## Prerequisites

- **.NET SDK 8 or later.** The projects target `net8.0`. If only .NET 9 or later is installed, set `DOTNET_ROLL_FORWARD=Major` when running the server.
- **Node.js 18 or later**, for the stub and the tests.
- For the automated tests only: `npm install`, then `npx playwright install chromium`.

All commands run from the repo root.

---

## Option 1: Run offline (no LaunchDarkly account)

Terminal 1, the fake LaunchDarkly service:

```sh
node tools/ld-stub/stub.js 5199
```

Terminal 2, build and start the app:

```sh
dotnet publish src/BlazorLdPoc.Server -c Release -o out
cd out
ASPNETCORE_ENVIRONMENT=Stub ASPNETCORE_URLS=http://localhost:5080 dotnet BlazorLdPoc.Server.dll
```

Open http://localhost:5080. Then:

- **Live update:** push a flag change, and the banner updates without a reload:
  ```sh
  curl -X POST "http://localhost:5199/_stub/patch?key=demo-banner&value=Updated%20live"
  ```
- **Identify:** click Alice, Bob or Anonymous. The flags re-evaluate per user.
- **Track:** click the track button. The stub records the event at http://localhost:5199/_stub/log.

To try server bootstrap offline, restart the server with `ASPNETCORE_ENVIRONMENT=StubBootstrap`.

---

## Option 2: Run against your LaunchDarkly environment

About 15 minutes. UI labels may differ slightly between LaunchDarkly accounts; each step links to the official docs.

### Step 1: Pick a project and environment

Use a **non-production environment** (for example `Test` or `Staging`) in an existing project, or create a new project
for this PoC. Flags exist in every environment of a project, but targeting and keys are per environment.

### Step 2: Copy your client-side ID

Find it under **Settings → SDK keys** (or the project's **Environments** list), for the environment you picked.

| Key | Looks like | Used for | Secret? |
|---|---|---|---|
| **Client-side ID** | 24 hex characters | The browser app (this step) | **No.** It ships to every browser. |
| **SDK key** | `sdk-…` | The server, only for Option 3 | **Yes.** Never put it in a file, source control, or client config. |

The mobile key is not used.

### Step 3: Create the four flags

The app reads four flags. For each one ([docs: Creating new flags](https://launchdarkly.com/docs/home/flags/new)):

1. Click **Create → Flag**.
2. Enter the **Name**, and make sure the **Key** matches the list below exactly. Keys can't be changed later.
3. Choose the **Variations** type and enter the variations listed below.
4. Set the **default on** and **default off** variations as listed below.
5. **Make it available to client-side SDKs:** make sure **SDKs using client-side ID** is enabled (it may already be on by
   default). Without this, the browser silently gets the code's fallback values.
6. Click **Create flag**.

**`demo-banner`** · type **String**
- Variation 1: `Hello from LaunchDarkly` → **default off**
- Variation 2: `Updated live from LaunchDarkly` → **default on**

**`max-items`** · type **Number**
- Variation 1: `5` → **default off**
- Variation 2: `10` → **default on**

**`theme-config`** · type **JSON**
- Variation 1: `{"color":"#3b82f6","rounded":false}` → **default off**
- Variation 2: `{"color":"#10b981","rounded":true}` → **default on**

**`new-checkout`** · type **Boolean**
- Variations `true` / `false` → **default off:** `false`, **default on:** `false` (Step 4 adds a rule that serves `true` to one user)

Each flag's **off** value differs from the code's fallback (`"(default banner)"`, `3`, `null`), so the page proves the
values come from LaunchDarkly even while the flags are off.

To change client-side availability on an existing flag, open it and use **Advanced controls** in the right sidebar.
To make it the default for new flags, use the project's flag settings
([docs](https://launchdarkly.com/docs/home/flags/proj-flag-settings#client-side-availability)).

### Step 4: Add a targeting rule to `new-checkout`

([docs: Target with flags](https://launchdarkly.com/docs/home/flags/target))

1. Go to **Code → Flags**, open `new-checkout`, and select your environment.
2. On the **Targeting** tab, add a rule: context kind **user**, attribute **key**, operator **is one of**, value
   `user-123`, serve **`true`**.
3. Leave the default rule serving **`false`**.
4. Turn the flag **On**, then click **Review and save**.

Result: Alice (`user-123`) gets the new checkout; Bob (`user-456`) and anonymous users don't.

### Step 5: Configure and start the app

Put the client-side ID in `src/BlazorLdPoc.Client/wwwroot/appsettings.json`:

```json
{
  "LaunchDarkly": {
    "ClientSideId": "YOUR-CLIENT-SIDE-ID",
    "InitTimeoutSeconds": 5,
    "LogLevel": "info"
  }
}
```

Start the app, and open the URL it prints:

```sh
dotnet run --project src/BlazorLdPoc.Server
```

### Step 6: Verify each feature

Keep the app open in one tab and LaunchDarkly in another.

| # | Do this | Expect |
|---|---|---|
| 1 | Load the app | Init status `complete`. Banner "Hello from LaunchDarkly", max items `5`, blue theme, "Classic checkout". These are the flags' **off** values, so they came from LaunchDarkly. |
| 2 | In LaunchDarkly, turn **On** `demo-banner`, `max-items` and `theme-config` | The open page updates **without a reload**: "Updated live from LaunchDarkly", `10`, green/rounded. The change counter goes up. |
| 3 | Click **Identify Alice** | "New checkout is ON" (matches the Step 4 rule) |
| 4 | Click **Identify Bob** | "Classic checkout" |
| 5 | Click **Track "checkout-clicked"**. In LaunchDarkly, open **Code → Live events** and filter to **Custom** | A `checkout-clicked` event with `metricValue: 42.5`. The page refreshes about every 30 seconds. ([docs](https://launchdarkly.com/docs/home/releases/live-events)) |
| 6 | Open the **Contexts** list in LaunchDarkly | `user-123` (Alice) and `user-456` (Bob) appear |

---

## Option 3: Add server bootstrap (recommended)

The server decides the user, evaluates their flags with the .NET server SDK, and computes the secure-mode hash. The
browser then renders correct values on the first frame, and the SDK key never leaves the server. Complete Option 2 first.

1. In the client `appsettings.json`, add:
   ```json
   "BootstrapUrl": "api/launchdarkly/bootstrap"
   ```
2. Copy the environment's **SDK key** (`sdk-…`) from the same place as the client-side ID. Start the server with it as
   an environment variable, so it never lands in a file or your shell history:
   ```sh
   read -s "LD_SDK_KEY?Paste LaunchDarkly SDK key: "; echo     # zsh; in bash: read -s -p "Paste key: " LD_SDK_KEY
   LaunchDarkly__SdkKey="$LD_SDK_KEY" dotnet run --project src/BlazorLdPoc.Server
   ```
3. Verify:

| Do this | Expect |
|---|---|
| Load the app | Starts as Alice (`user-123`, decided by the server) with correct values on the first render |
| Open `/api/launchdarkly/server-eval` | JSON with the same flag values, evaluated by the .NET server SDK (what backend code sees) |
| Open `/api/launchdarkly/bootstrap` | The client-side flags, `$flagsState` metadata and a `hash` (what the browser receives) |
| Turn a flag off in LaunchDarkly, then refresh `server-eval` | The new value, with no server restart |

Without an SDK key, the server uses built-in offline test data instead of LaunchDarkly.

The demo user is fixed in `src/BlazorLdPoc.Server/FlagContextFactory.cs`. In a real app, build the context there from
the signed-in user, never from a key the browser sends.

---

## Optional: secure mode, metrics

**Secure mode** stops a browser from requesting flags for a user it isn't signed in as
([docs](https://launchdarkly.com/docs/sdk/features/secure-mode)). Turn it on by editing your environment in the
project's **Environments** list, and run with server bootstrap (Option 3), which supplies the hash. With secure mode on,
the demo's Identify buttons stop working, because the browser has no server-issued hash for those users. In a real app,
the server issues a hash whenever the signed-in user changes. This was not part of the tested scope.

**Metrics:** to use `checkout-clicked` in experiments or guarded rollouts, open **Data → Event explorer**, click
`checkout-clicked`, and choose **Create new metric** ([docs](https://launchdarkly.com/docs/home/metrics/view-incoming-events)).

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| Page shows `(default banner)`, `3` or `(default)` | The flag isn't available to client-side SDKs (Option 2, Step 3), the key is misspelled, or the client-side ID is for another environment. **Live events** lists such keys under "Misconfigured flag keys". |
| Values show but don't update live | A proxy, firewall or browser extension blocks long-lived connections to `clientstream.launchdarkly.com` |
| `new-checkout` never turns on for Alice | The flag isn't **On**, or the rule doesn't use context kind **user**, attribute **key**, value `user-123` |
| No events in Live events | Wait for the 30-second refresh, check the environment selected, and check the browser console for blocked requests to `events.launchdarkly.com` |
| Server shows "Hello from the .NET server SDK (bootstrap)" | The server was started without `LaunchDarkly__SdkKey`, so it's using offline test data |

---

## Automated tests (no account needed)

With the stub running and the server started as in Option 1:

```sh
node tools/verify/verify.js stub          # 17 checks
# restart the server with ASPNETCORE_ENVIRONMENT=StubBootstrap, then:
node tools/verify/verify.js bootstrap     # 7 checks
```

## Reproduce the .NET client SDK failure

With the stub running:

```sh
dotnet run --project repro/DotnetClientSdkRepro
```

Open the printed URL with `?a=A` through `?a=G` (one attempt per page load) and read the browser console.
Attempts B and C intentionally hang the page. Recorded results are in `repro/results`.

## Configuration reference

| Setting | Where | Notes |
|---|---|---|
| `LaunchDarkly:ClientSideId` | Client `wwwroot/appsettings.json` | Required for a real environment |
| `LaunchDarkly:BootstrapUrl` | Client `wwwroot/appsettings.json` | Optional; turns on server bootstrap |
| `LaunchDarkly:InitTimeoutSeconds` | Client `wwwroot/appsettings.json` | Default `5` |
| `LaunchDarkly:SdkModuleUrl` | Client `wwwroot/appsettings.json` | Optional; load the JS SDK from a CDN instead of the vendored copy, for example `https://unpkg.com/@launchdarkly/js-client-sdk@4.13.0/dist/index.js`. Vendoring is preferred (no third-party runtime dependency, CSP-friendly, reviewed upgrades). |
| `LaunchDarkly:SdkKey` | Server, as env var `LaunchDarkly__SdkKey` | Secret. Optional; without it the server uses offline test data |
| `ASPNETCORE_ENVIRONMENT=Stub` / `StubBootstrap` | Server | Points the client at the local stub (`appsettings.Stub*.json`) |

## Cleanup

Archive the four flags when finished (flag → **Settings** → **Archive**), and remove your client-side ID from
`appsettings.json` before sharing the code.

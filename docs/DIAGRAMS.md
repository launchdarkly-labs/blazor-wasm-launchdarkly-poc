# LaunchDarkly in Blazor WebAssembly

## 1. Today: the .NET client-side SDK inside Blazor WebAssembly

```mermaid
flowchart TD
    APP["Blazor WebAssembly app<br/>(.NET runs in the browser, single thread)"] --> SDK["LaunchDarkly.ClientSdk<br/>LdClient.InitAsync()"]

    SDK --> A["A: Default configuration"]
    A -->|throws| A1["PlatformNotSupportedException"]

    SDK --> W["Workarounds<br/>browser fetch handler + no persistence"]

    W --> BC["B / C: streaming or polling<br/>with events"]
    BC --> BC1["InitAsync hangs<br/>(event processor blocks the only thread)"]

    W --> D["D: polling, no events"]
    D --> D1["Works, but no live updates<br/>and no events"]

    W --> E["E: streaming, no events"]
    E --> E1["Never initializes<br/>(browser buffers the stream)"]
    E1 --> F["F: turn on fetch response streaming<br/>for every request"]
    F --> F1["Streaming works<br/>events still off"]
    F1 --> G1["G: live updates and IdentifyAsync work"]
    F1 --> G2["G: synchronous Identify() returns false<br/>(can't block the single WASM thread)"]

    A1 --> X["Not usable out of the box<br/>launchdarkly/dotnet-core#58 (open)"]
    BC1 --> X
    D1 --> X
    G2 --> X

    classDef bad fill:#fde2e2,stroke:#c0392b,color:#000
    classDef partial fill:#fff4d6,stroke:#b7791f,color:#000
    classDef good fill:#e3f6e8,stroke:#2f855a,color:#000
    class A1,BC1,E1,G2,X bad
    class D1,F1 partial
    class G1 good
```

*Diagram not showing? Open the image: [diagram-1-dotnet-client-sdk.png](diagram-1-dotnet-client-sdk.png)*

**Best case (F/G):** streaming flags with **events off**: no analytics, metrics, experiments or guarded rollouts, and async-only identify.

## 2. Proposed: LaunchDarkly's JavaScript SDK behind a C# facade

```mermaid
flowchart TD
    subgraph srv["ASP.NET Core server (optional: not needed for the fix)"]
        direction TB
        SS["LaunchDarkly .NET server SDK"] --> BE["/api/launchdarkly/bootstrap<br/>context + secure-mode hash + flags"]
    end

    subgraph br["Browser: Blazor WebAssembly (the fix)"]
        direction TB
        PG["Program.cs<br/>initializes LaunchDarkly before first render"]
        RC["Razor components<br/>FeatureFlag.razor re-renders on change"]
        FAC["LaunchDarklyClient (C# facade)<br/>Bool / String / Int / Double / Json variations"]
        JS["launchdarkly-interop.js"]
        JSSDK["LaunchDarkly JS SDK<br/>@launchdarkly/js-client-sdk 4.13.0 (vendored)"]

        PG -->|"① initializes"| FAC
        RC <-->|"② ↓ read flag values · track / identify / flush<br/>③ ↑ FlagChanged event → re-render"| FAC
        FAC <-->|"② ↓ in-process JS interop (sync reads)<br/>③ ↑ [JSInvokable] OnFlagsChanged"| JS
        JS <-->|"② ↓ calls the SDK<br/>③ ↑ change event"| JSSDK
    end

    BE -->|"①b initial flags, no flicker"| PG
    JSSDK <-->|"① get flags + open stream · ③ stream pushes changes<br/>events (browser-native fetch, client-side ID)"| LD[("LaunchDarkly")]
    SS <-->|"flag rules stream<br/>(SDK key, server only)"| LD
```

*Diagram not showing? Open the image: [diagram-2-js-sdk-facade.png](diagram-2-js-sdk-facade.png)*

**Step-by-step views.** The numbers match the phase diagrams in `before-after.html`:

| Label | Phase | What happens |
|---|---|---|
| **①** | Client Phase 1 · Startup | The app sets up LaunchDarkly before showing anything. The JS SDK gets flag values and opens the stream. |
| **①b** | Client Phase 1b · Startup with server bootstrap (optional) | The server evaluates the flags and the hash first, so the browser starts with correct values. |
| **②** | Client Phase 2 · First render | A component reads a flag. The ↓ arrows return a value from memory, with no network call. |
| **③** | Client Phase 3 · A flag changes | LaunchDarkly pushes the change. The ↑ arrows carry it to C#, and the page re-renders. |
| Server line | Server Phases 1–3 | The server SDK downloads the flag rules, evaluates them in memory per request, and updates them when they change. |

| Why it works | |
|---|---|
| **Built for the browser** | The JS SDK already handles fetch, streaming, events and flushing on page close. None of the .NET workarounds are needed. |
| **Nothing blocks the thread** | Flag reads are synchronous in-process interop. Identify, track and flush are async. |
| **Live updates reach the UI** | The JS SDK's change event calls into C#, and components re-render. |
| **No flicker, no secrets in the browser** | The server pre-evaluates flags and computes the secure-mode hash. The SDK key never leaves the server. |
| **Swappable later** | Components depend only on the C# interface. A native .NET SDK could replace the JS SDK later without changing components. |

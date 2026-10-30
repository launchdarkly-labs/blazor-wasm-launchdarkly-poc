using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;

namespace BlazorLdPoc.Client.LaunchDarkly;

/// <summary>
/// C# facade over the LaunchDarkly JS client-side SDK (wwwroot/js/launchdarkly-interop.js).
/// Register as a singleton in Blazor WebAssembly (IJSRuntime is a singleton there; in Blazor Server it is scoped,
/// so this class would have to be scoped and could not use the synchronous methods).
/// </summary>
public sealed class LaunchDarklyClient : ILaunchDarklyClient
{
    private const string ModulePath = "./js/launchdarkly-interop.js";

    private readonly IJSRuntime _js;
    private readonly LaunchDarklyOptions _options;
    private readonly IWebAssemblyHostEnvironment _hostEnv;
    private readonly ILogger<LaunchDarklyClient> _log;
    private readonly Dictionary<string, List<Action>> _perFlagCallbacks = new();
    private IJSInProcessObjectReference? _module;
    private DotNetObjectReference<LaunchDarklyClient>? _selfRef;

    public LaunchDarklyClient(IJSRuntime js, IOptions<LaunchDarklyOptions> options,
        IWebAssemblyHostEnvironment hostEnv, ILogger<LaunchDarklyClient> log)
    {
        _js = js;
        _options = options.Value;
        _hostEnv = hostEnv;
        _log = log;
    }

    public bool Initialized => _module is not null && InitResult is not null;
    public LdInitResult? InitResult { get; private set; }
    public event EventHandler<FlagsChangedEventArgs>? FlagChanged;
    public event EventHandler<string>? Error;

    public async Task<LdInitResult> InitializeAsync(object? context = null, CancellationToken ct = default)
    {
        // In WASM the module reference returned by "import" implements IJSInProcessObjectReference,
        // which enables the synchronous Invoke<T> calls used by the *Variation methods below.
        _module ??= await _js.InvokeAsync<IJSInProcessObjectReference>("import", ct, ModulePath);
        _selfRef ??= DotNetObjectReference.Create(this);

        object? bootstrap = null;
        string? hash = null;
        if (!string.IsNullOrWhiteSpace(_options.BootstrapUrl))
        {
            try
            {
                using var http = new HttpClient { BaseAddress = new Uri(_hostEnv.BaseAddress) };
                var payload = await http.GetFromJsonAsync<LdBootstrapPayload>(_options.BootstrapUrl, ct);
                if (payload is not null)
                {
                    context = payload.Context;   // context is decided server-side (from the authenticated user)
                    hash = payload.Hash;         // secure-mode hash, computed with the SDK key on the server
                    bootstrap = payload.Flags;   // AllFlagsState(...) serialized by the .NET server SDK
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "LaunchDarkly bootstrap fetch failed; falling back to network initialization");
            }
        }

        context ??= LdContexts.Anonymous();
        var init = new Dictionary<string, object?>
        {
            ["timeoutSeconds"] = _options.InitTimeoutSeconds,
            ["bootstrap"] = bootstrap,
            ["hash"] = hash,
            ["sdkUrl"] = _options.SdkModuleUrl,
        };

        InitResult = await _module.InvokeAsync<LdInitResult>("initialize", ct,
            _options.ClientSideId, context, _options.ToJsOptions(), _selfRef, init);
        if (!InitResult.IsComplete)
            _log.LogWarning("LaunchDarkly init status {Status}: {Error}. Variations return cached/default values.",
                InitResult.Status, InitResult.Error);
        return InitResult;
    }

    // ---- Typed variations (synchronous, in-process interop; WASM only) -----------------------------

    public bool BoolVariation(string key, bool defaultValue) =>
        _module is null ? defaultValue : _module.Invoke<bool>("boolVariation", key, defaultValue);

    public string StringVariation(string key, string defaultValue) =>
        _module is null ? defaultValue : _module.Invoke<string?>("stringVariation", key, defaultValue) ?? defaultValue;

    // JS has a single number type; the SDK's numberVariation is used and the result truncated to int.
    public int IntVariation(string key, int defaultValue) =>
        _module is null ? defaultValue : (int)_module.Invoke<double>("numberVariation", key, defaultValue);

    public double DoubleVariation(string key, double defaultValue) =>
        _module is null ? defaultValue : _module.Invoke<double>("numberVariation", key, defaultValue);

    public T? JsonVariation<T>(string key, T? defaultValue)
    {
        if (_module is null) return defaultValue;
        // Fetch as JsonElement and deserialize here, so a shape mismatch falls back to the default
        // instead of throwing out of the interop layer.
        var raw = _module.Invoke<JsonElement?>("jsonVariation", key, null);
        if (raw is null || raw.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return defaultValue;
        try { return raw.Value.Deserialize<T>(JsonOptions); }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "Flag {Key} could not be deserialized to {Type}", key, typeof(T).Name);
            return defaultValue;
        }
    }

    public LdEvaluationDetail? VariationDetail(string key, object? defaultValue) =>
        _module?.Invoke<LdEvaluationDetail>("variationDetail", key, defaultValue);

    public IReadOnlyDictionary<string, JsonElement> AllFlags() =>
        _module?.Invoke<Dictionary<string, JsonElement>>("allFlags") ?? new Dictionary<string, JsonElement>();

    public JsonElement? GetContext() => _module?.Invoke<JsonElement?>("getContext");

    public async ValueTask<T?> VariationAsync<T>(string key, T? defaultValue)
    {
        if (_module is null) return defaultValue;
        var raw = await _module.InvokeAsync<JsonElement?>("variation", key, defaultValue);
        if (raw is null || raw.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return defaultValue;
        try { return raw.Value.Deserialize<T>(JsonOptions); }
        catch (JsonException) { return defaultValue; }
    }

    // ---- Change notifications -------------------------------------------------------------------

    [JSInvokable]
    public void OnFlagsChanged(string[] keys)
    {
        _log.LogDebug("LaunchDarkly flags changed: {Keys}", string.Join(",", keys));
        FlagChanged?.Invoke(this, new FlagsChangedEventArgs(keys));
    }

    [JSInvokable]
    public void OnFlagChanged(string key)
    {
        if (_perFlagCallbacks.TryGetValue(key, out var callbacks))
            foreach (var cb in callbacks.ToArray()) cb();
    }

    [JSInvokable]
    public void OnError(string message)
    {
        _log.LogWarning("LaunchDarkly SDK error: {Message}", message);
        Error?.Invoke(this, message);
    }

    public IDisposable OnFlagChange(string key, Action callback)
    {
        if (!_perFlagCallbacks.TryGetValue(key, out var list))
        {
            _perFlagCallbacks[key] = list = new List<Action>();
            _module?.InvokeVoid("subscribe", key);
        }
        list.Add(callback);
        return new Subscription(() =>
        {
            list.Remove(callback);
            if (list.Count == 0)
            {
                _perFlagCallbacks.Remove(key);
                _module?.InvokeVoid("unsubscribe", key);
            }
        });
    }

    // ---- Context, events ------------------------------------------------------------------------

    public async Task<LdIdentifyResult> IdentifyAsync(object context, string? hash = null, int? timeoutSeconds = null)
    {
        EnsureModule();
        // Note: in secure mode every new context needs a hash computed server-side (see Server project).
        return await _module!.InvokeAsync<LdIdentifyResult>("identify", context, hash, null, timeoutSeconds);
    }

    public async Task TrackAsync(string eventKey, object? data = null, double? metricValue = null)
    {
        EnsureModule();
        await _module!.InvokeVoidAsync("track", eventKey, data, metricValue);
    }

    public async Task<LdFlushResult> FlushAsync()
    {
        EnsureModule();
        return await _module!.InvokeAsync<LdFlushResult>("flush");
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try
            {
                await _module.InvokeVoidAsync("close");
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException) { /* page already gone */ }
            _module = null;
        }
        _selfRef?.Dispose();
        _selfRef = null;
    }

    private void EnsureModule()
    {
        if (_module is null) throw new InvalidOperationException("Call InitializeAsync() before using the LaunchDarkly client.");
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() { _dispose?.Invoke(); _dispose = null; }
    }
}

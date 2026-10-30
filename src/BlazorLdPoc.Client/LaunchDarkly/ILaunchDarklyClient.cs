using System.Text.Json;

namespace BlazorLdPoc.Client.LaunchDarkly;

/// <summary>
/// The surface components depend on. Mock this in bUnit/unit tests instead of touching JS.
/// Synchronous variation methods use in-process interop (Blazor WebAssembly only).
/// </summary>
public interface ILaunchDarklyClient : IAsyncDisposable
{
    bool Initialized { get; }
    LdInitResult? InitResult { get; }
    /// <summary>Raised (on the WASM UI thread) when the JS SDK reports changed flags, e.g. from the stream or after identify.</summary>
    event EventHandler<FlagsChangedEventArgs>? FlagChanged;
    event EventHandler<string>? Error;

    Task<LdInitResult> InitializeAsync(object? context = null, CancellationToken ct = default);

    bool BoolVariation(string key, bool defaultValue);
    string StringVariation(string key, string defaultValue);
    int IntVariation(string key, int defaultValue);
    double DoubleVariation(string key, double defaultValue);
    T? JsonVariation<T>(string key, T? defaultValue);
    LdEvaluationDetail? VariationDetail(string key, object? defaultValue);
    IReadOnlyDictionary<string, JsonElement> AllFlags();
    /// <summary>The SDK's current context (including a generated key for anonymous contexts).</summary>
    JsonElement? GetContext();

    /// <summary>Async equivalent that also works where only async interop exists (Blazor Server / Auto).</summary>
    ValueTask<T?> VariationAsync<T>(string key, T? defaultValue);

    /// <summary>Calls <paramref name="callback"/> when one flag changes (JS <c>change:key</c>). Dispose to unsubscribe.</summary>
    IDisposable OnFlagChange(string key, Action callback);

    Task<LdIdentifyResult> IdentifyAsync(object context, string? hash = null, int? timeoutSeconds = null);
    Task TrackAsync(string eventKey, object? data = null, double? metricValue = null);
    Task<LdFlushResult> FlushAsync();
}

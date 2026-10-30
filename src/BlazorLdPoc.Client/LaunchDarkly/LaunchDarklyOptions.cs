namespace BlazorLdPoc.Client.LaunchDarkly;

/// <summary>Bound from the "LaunchDarkly" section of wwwroot/appsettings*.json.</summary>
public sealed class LaunchDarklyOptions
{
    /// <summary>Client-side ID (environment ID). Public by design; never put an SDK key in the browser.</summary>
    public string ClientSideId { get; set; } = "";

    /// <summary>Optional: relative URL of a backend endpoint returning { context, hash, flags } (see Server project).</summary>
    public string? BootstrapUrl { get; set; }

    public int InitTimeoutSeconds { get; set; } = 5;

    /// <summary>Optional override for the SDK module URL (e.g. a CDN). Default is the vendored copy in wwwroot/lib.</summary>
    public string? SdkModuleUrl { get; set; }

    // A subset of LDOptions. Nulls are not sent, so the SDK defaults apply.
    public string? BaseUri { get; set; }
    public string? StreamUri { get; set; }
    public string? EventsUri { get; set; }
    public bool? Streaming { get; set; }
    public bool? SendEvents { get; set; }
    public bool? WithReasons { get; set; }
    public bool? FetchGoals { get; set; }
    public int? FlushIntervalSeconds { get; set; }
    public bool? AllAttributesPrivate { get; set; }
    public string? LogLevel { get; set; }

    internal Dictionary<string, object> ToJsOptions()
    {
        var o = new Dictionary<string, object>();
        void Add(string name, object? value) { if (value is not null) o[name] = value; }
        Add("baseUri", BaseUri);
        Add("streamUri", StreamUri);
        Add("eventsUri", EventsUri);
        Add("streaming", Streaming);
        Add("sendEvents", SendEvents);
        Add("withReasons", WithReasons);
        Add("fetchGoals", FetchGoals);
        Add("flushInterval", FlushIntervalSeconds);
        Add("allAttributesPrivate", AllAttributesPrivate);
        Add("logLevel", LogLevel);
        return o;
    }
}

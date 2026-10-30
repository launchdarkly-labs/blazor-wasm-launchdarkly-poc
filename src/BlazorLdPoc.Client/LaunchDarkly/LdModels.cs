using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlazorLdPoc.Client.LaunchDarkly;

/// <summary>Helpers for building LDContext JSON. Anything System.Text.Json can serialize to a valid context works.</summary>
public static class LdContexts
{
    public static Dictionary<string, object?> User(string key, string? name = null, IDictionary<string, object?>? attributes = null)
    {
        var ctx = new Dictionary<string, object?> { ["kind"] = "user", ["key"] = key };
        if (name is not null) ctx["name"] = name;
        if (attributes is not null) foreach (var (k, v) in attributes) ctx[k] = v;
        return ctx;
    }

    /// <summary>Anonymous user; the JS SDK generates and persists (localStorage) a key when none is given.</summary>
    public static Dictionary<string, object?> Anonymous() => new() { ["kind"] = "user", ["anonymous"] = true };
}

public sealed record FlagsChangedEventArgs(IReadOnlyList<string> Keys);

public sealed record LdInitResult(string Status, string? Error)
{
    public bool IsComplete => Status == "complete";
}

public sealed record LdIdentifyResult(string Status, string? Error);

public sealed record LdFlushResult(bool Result, string? Error);

public sealed class LdEvaluationReason
{
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("errorKind")] public string? ErrorKind { get; set; }
    [JsonPropertyName("ruleIndex")] public int? RuleIndex { get; set; }
    [JsonPropertyName("ruleId")] public string? RuleId { get; set; }
    [JsonPropertyName("inExperiment")] public bool? InExperiment { get; set; }
}

public sealed class LdEvaluationDetail
{
    [JsonPropertyName("value")] public JsonElement Value { get; set; }
    [JsonPropertyName("variationIndex")] public int? VariationIndex { get; set; }
    [JsonPropertyName("reason")] public LdEvaluationReason? Reason { get; set; }
}

/// <summary>Shape returned by the backend bootstrap endpoint.</summary>
public sealed class LdBootstrapPayload
{
    [JsonPropertyName("context")] public JsonElement Context { get; set; }
    [JsonPropertyName("hash")] public string? Hash { get; set; }
    [JsonPropertyName("flags")] public JsonElement Flags { get; set; }
}

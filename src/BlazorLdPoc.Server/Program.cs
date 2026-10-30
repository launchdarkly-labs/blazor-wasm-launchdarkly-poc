using System.Text.Json;
using BlazorLdPoc.Server;
using LaunchDarkly.Sdk;
using LaunchDarkly.Sdk.Json;
using LaunchDarkly.Sdk.Server;
using LaunchDarkly.Sdk.Server.Integrations;
using LaunchDarkly.Sdk.Server.Interfaces;

var builder = WebApplication.CreateBuilder(args);

// The backend keeps using the normal LaunchDarkly .NET *server-side* SDK. It is a separate client from
// the browser one: separate connection, separate event stream, same environment.
var sdkKey = builder.Configuration["LaunchDarkly:SdkKey"];
var useTestData = string.IsNullOrWhiteSpace(sdkKey);
builder.Services.AddSingleton<ILdClient>(_ =>
{
    if (!useTestData)
        return new LdClient(Configuration.Builder(sdkKey!).StartWaitTime(TimeSpan.FromSeconds(5)).Build());

    // Local/offline mode for the PoC: TestData instead of a real environment. The "SDK key" is only used
    // to compute secure-mode hashes, which the local stub verifies with the same key.
    var td = TestData.DataSource();
    td.Update(td.Flag("demo-banner").ValueForAll(LdValue.Of("Hello from the .NET server SDK (bootstrap)")));
    td.Update(td.Flag("new-checkout").BooleanFlag().VariationForAll(false).VariationForUser("user-123", true));
    td.Update(td.Flag("max-items").ValueForAll(LdValue.Of(10)));
    td.Update(td.Flag("theme-config").ValueForAll(LdValue.Parse("{\"color\":\"#0b7a75\",\"rounded\":true}")));
    return new LdClient(Configuration.Builder(builder.Configuration["LaunchDarkly:TestSdkKey"] ?? "sdk-local-test-key")
        .DataSource(td).Events(Components.NoEvents).Build());
});

var app = builder.Build();

app.UseBlazorFrameworkFiles();   // serves the WASM app (and sends the Blazor-Environment header)
app.UseStaticFiles();

// Bootstrap endpoint: evaluates all client-side flags for the current user on the server, so the WASM app
// can render correct values on first paint, and returns the secure-mode hash for that context.
app.MapGet("/api/launchdarkly/bootstrap", (HttpContext http, ILdClient ld) =>
{
    var context = FlagContextFactory.ForRequest(http);

    var state = useTestData
        ? ld.AllFlagsState(context)                                   // TestData flags aren't marked client-side
        : ld.AllFlagsState(context, FlagsStateOption.ClientSideOnly); // only flags exposed to client-side SDKs

    return Results.Content(JsonSerializer.Serialize(new
    {
        // LdJsonSerialization produces the JSON shapes the JS SDK expects
        context = JsonDocument.Parse(LdJsonSerialization.SerializeObject(context)).RootElement,
        hash = ld.SecureModeHash(context),
        flags = JsonDocument.Parse(LdJsonSerialization.SerializeObject(state)).RootElement,
    }), "application/json");
});

// Server-side evaluation: what backend code (APIs, Blazor Server) sees for the same context.
// Unlike the bootstrap endpoint it returns ALL flags, not just those exposed to client-side SDKs.
app.MapGet("/api/launchdarkly/server-eval", (HttpContext http, ILdClient ld) =>
{
    var context = FlagContextFactory.ForRequest(http);
    var values = ld.AllFlagsState(context).ToValuesJsonMap();

    return Results.Content(JsonSerializer.Serialize(new
    {
        context = JsonDocument.Parse(LdJsonSerialization.SerializeObject(context)).RootElement,
        flags = values.ToDictionary(kv => kv.Key, kv => JsonDocument.Parse(kv.Value.ToJsonString()).RootElement),
    }), "application/json");
});

app.MapFallbackToFile("index.html");
app.Run();

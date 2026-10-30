// Repro: LaunchDarkly .NET *client-side* SDK (LaunchDarkly.ClientSdk 5.10.2) inside Blazor WebAssembly.
// Points at the local stub (tools/ld-stub, http://localhost:5199) so no real mobile key is needed.
//   A: default configuration (what a customer would try first)
//   B: workarounds - browser-compatible HttpClientHandler + no persistence, streaming data source
//   C: same as B with the polling data source; D: C without events; E: B without events
//   G: F config kept alive: live stream patch, IdentifyAsync, then sync Identify
//   F: E, but with fetch response streaming enabled per request (SetBrowserResponseStreamingEnabled)
using DotnetClientSdkRepro;
using LaunchDarkly.Sdk;
using LaunchDarkly.Sdk.Client;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using LdConfig = LaunchDarkly.Sdk.Client.Configuration;
using LdConfigBuilder = LaunchDarkly.Sdk.Client.ConfigurationBuilder;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
var host = builder.Build();

LdConfigBuilder Base() => LdConfig.Builder("mob-fake-key", LdConfigBuilder.AutoEnvAttributes.Disabled)
    .ServiceEndpoints(Components.ServiceEndpoints().RelayProxy("http://localhost:5199"));

// Pick one attempt per page load (?a=A|B|C|D): a failing attempt can starve the single WASM thread.
var which = ((Microsoft.JSInterop.IJSInProcessRuntime)host.Services.GetRequiredService<Microsoft.JSInterop.IJSRuntime>())
    .Invoke<string>("eval", "new URLSearchParams(location.search).get('a') || 'A'");
LdConfigBuilder Browserish() => Base()
    .Http(Components.HttpConfiguration().MessageHandler(new HttpClientHandler())) // BrowserHttpHandler (fetch)
    .Persistence(Components.NoPersistence);                                           // no IsolatedStorage
switch (which)
{
    case "A": await Attempt("A default", Base().Build()); break;
    case "B": await Attempt("B browser handler + streaming", Browserish().Build()); break;
    case "C": await Attempt("C browser handler + polling", Browserish().DataSource(Components.PollingDataSource()).Build()); break;
    case "D": await Attempt("D browser handler + polling + no events", Browserish().DataSource(Components.PollingDataSource()).Events(Components.NoEvents).Build()); break;
    case "E": await Attempt("E browser handler + streaming + no events", Browserish().Events(Components.NoEvents).Build()); break;
    case "F": await Attempt("F streaming-enabled fetch handler + streaming + no events", Base()
        .Http(Components.HttpConfiguration().MessageHandler(new BrowserStreamingHandler { InnerHandler = new HttpClientHandler() }))
        .Persistence(Components.NoPersistence).Events(Components.NoEvents).Build()); break;
    case "G": await LiveUpdatesAndIdentify(Base()
        .Http(Components.HttpConfiguration().MessageHandler(new BrowserStreamingHandler { InnerHandler = new HttpClientHandler() }))
        .Persistence(Components.NoPersistence).Events(Components.NoEvents).Build()); break;
}
Console.WriteLine("REPRO: done");

await host.RunAsync();

static async Task LiveUpdatesAndIdentify(LdConfig config)
{
    const string n = "G";
    var client = await LdClient.InitAsync(config, Context.New("user-123"), TimeSpan.FromSeconds(4));
    Console.WriteLine($"REPRO[{n}]: Initialized={client.Initialized} demo-banner='{client.StringVariation("demo-banner", "?")}'");
    var changed = new TaskCompletionSource<string>();
    client.FlagTracker.FlagValueChanged += (_, e) => { Console.WriteLine($"REPRO[{n}]: FlagValueChanged {e.Key}: {e.OldValue} -> {e.NewValue}"); changed.TrySetResult(e.Key); };
    Console.WriteLine($"REPRO[{n}]: waiting for stream patch (test pushes one) ...");
    var w = await Task.WhenAny(changed.Task, Task.Delay(15000));
    Console.WriteLine($"REPRO[{n}]: patch received={w == changed.Task} demo-banner='{client.StringVariation("demo-banner", "?")}'");
    try
    {
        var ok = await client.IdentifyAsync(Context.New("user-456"));
        Console.WriteLine($"REPRO[{n}]: IdentifyAsync(user-456)={ok} max-items={client.IntVariation("max-items", -1)}");
    }
    catch (Exception ex) { Console.WriteLine($"REPRO[{n}]: IdentifyAsync EXCEPTION {ex.GetType().Name}: {ex.Message}"); }
    try
    {
        var ok = client.Identify(Context.New("user-123"), TimeSpan.FromSeconds(2));
        Console.WriteLine($"REPRO[{n}]: sync Identify(user-123)={ok} max-items={client.IntVariation("max-items", -1)}");
    }
    catch (Exception ex) { Console.WriteLine($"REPRO[{n}]: sync Identify EXCEPTION {ex.GetType().FullName}: {ex.Message}"); }
}

static async Task Attempt(string name, LdConfig config)
{
    Console.WriteLine($"REPRO[{name}]: calling LdClient.InitAsync ...");
    try
    {
        var initTask = LdClient.InitAsync(config, Context.New("user-123"), TimeSpan.FromSeconds(4));
        var winner = await Task.WhenAny(initTask, Task.Delay(TimeSpan.FromSeconds(8)));
        if (winner != initTask) { Console.WriteLine($"REPRO[{name}]: InitAsync did not return within 8s (hung)"); return; }
        using var client = await initTask;
        var status = client.DataSourceStatusProvider.Status;
        Console.WriteLine($"REPRO[{name}]: Initialized={client.Initialized} State={status.State} " +
            $"LastError={(status.LastError is { } e ? e.Kind + " " + e.Message : "none")} " +
            $"max-items={client.IntVariation("max-items", -1)} new-checkout={client.BoolVariation("new-checkout", false)}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"REPRO[{name}]: EXCEPTION {ex.GetType().FullName}: {ex.Message}");
        Console.WriteLine(ex.ToString());
    }
}

// Blazor's fetch-backed handler buffers the whole response unless streaming is enabled per request,
// which an SSE client needs. (.NET 8: opt-in via WebAssemblyHttpRequestMessageExtensions.)
sealed class BrowserStreamingHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Microsoft.AspNetCore.Components.WebAssembly.Http.WebAssemblyHttpRequestMessageExtensions.SetBrowserResponseStreamingEnabled(request, true);
        return base.SendAsync(request, ct);
    }
}

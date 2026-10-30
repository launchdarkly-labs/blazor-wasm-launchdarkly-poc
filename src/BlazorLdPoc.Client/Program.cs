using BlazorLdPoc.Client;
using BlazorLdPoc.Client.LaunchDarkly;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

builder.Services.Configure<LaunchDarklyOptions>(builder.Configuration.GetSection("LaunchDarkly"));
builder.Services.AddSingleton<LaunchDarklyClient>();
builder.Services.AddSingleton<ILaunchDarklyClient>(sp => sp.GetRequiredService<LaunchDarklyClient>());

var host = builder.Build();

// Loading gate: initialize LaunchDarkly before the first render, so components never render with
// default values and then flip ("flicker"). The index.html loading spinner stays up meanwhile.
// Bounded by InitTimeoutSeconds; on timeout the app renders with cached/default values.
await host.Services.GetRequiredService<ILaunchDarklyClient>().InitializeAsync();

await host.RunAsync();

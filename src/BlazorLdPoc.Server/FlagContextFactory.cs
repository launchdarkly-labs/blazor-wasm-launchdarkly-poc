using LaunchDarkly.Sdk;

namespace BlazorLdPoc.Server;

/// <summary>
/// The single place that decides which LaunchDarkly context a request is evaluated with.
/// Both the browser bootstrap endpoint and server-side evaluation call this, so the same
/// user gets the same context key, kind and attributes on both tiers.
/// </summary>
public static class FlagContextFactory
{
    public static Context ForRequest(HttpContext http)
    {
        // PoC: fixed demo user. In production, build the context from the authenticated principal
        // (http.User) - never from a key the browser sends, or secure mode is pointless.
        return Context.Builder("user-123").Name("Alice").Set("plan", "gold").Build();
    }
}

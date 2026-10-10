namespace KHost.Plugins.YouTube.Tests;

/// <summary>Hands every named client the same stub handler and records the names asked for.</summary>
internal sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public List<string> Names { get; } = [];

    public HttpClient CreateClient(string name)
    {
        Names.Add(name);

        return new HttpClient(handler, disposeHandler: false);
    }
}

using System.Net;
using BaseLayer.Api.Providers;
using BaseLayer.Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BaseLayer.Tests;

public sealed class ContainerNetworkingTests
{
    private const string PublicOrigin = "http://localhost:18123";
    private static readonly ProviderTokens Tokens = new("access", "refresh", DateTime.UtcNow.AddHours(1));

    [Fact]
    public async Task BrowserUsesPublicOriginWhileAllAuthenticatedTrafficUsesContainerOrigin()
    {
        var handler = new Handler();
        using var http = new HttpClient(handler);
        var provider = new HomeAssistantProvider(http, Config(), new TestEnvironment());
        Assert.StartsWith(PublicOrigin + "/auth/authorize?", provider.AuthorizationUrl(PublicOrigin, "state"));
        await provider.ExchangeAsync(PublicOrigin, "code");
        await provider.RefreshAsync(PublicOrigin, Tokens);
        await provider.ReadAsync(PublicOrigin, Tokens);
        await provider.TurnOffAsync(PublicOrigin, Tokens, "switch.lamp");
        await provider.RevokeAsync(PublicOrigin, Tokens);
        Assert.Equal(new[] { "/auth/token", "/auth/token", "/api/states", "/api/services/switch/turn_off", "/auth/token" }, handler.Paths);
        Assert.All(handler.Origins, origin => Assert.Equal("http://homeassistant:8123", origin));
    }

    [Theory]
    [InlineData("http://homeassistant:8123")]
    [InlineData("http://localhost:9999")]
    [InlineData("http://localhost:18123/api")]
    public async Task MappingDoesNotBypassThePublicAllowlist(string origin)
    {
        var handler = new Handler();
        using var http = new HttpClient(handler);
        var provider = new HomeAssistantProvider(http, Config(), new TestEnvironment());
        await Assert.ThrowsAsync<ArgumentException>(() => provider.ReadAsync(origin, Tokens));
        Assert.Empty(handler.Paths);
    }

    [Theory]
    [InlineData("http://user:password@homeassistant:8123")]
    [InlineData("http://homeassistant:8123/api")]
    [InlineData("http://homeassistant:8123/?query=1")]
    [InlineData("ftp://homeassistant")]
    public async Task InvalidTransportConfigurationNeverReceivesCredentials(string target)
    {
        var handler = new Handler();
        using var http = new HttpClient(handler);
        var provider = new HomeAssistantProvider(http, Config(target), new TestEnvironment());
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ExchangeAsync(PublicOrigin, "code"));
        Assert.Empty(handler.Paths);
    }

    [Fact]
    public async Task ProductionCannotSendCredentialsOverAnHttpTransport()
    {
        const string origin = "https://ha.example.com";
        var handler = new Handler();
        using var http = new HttpClient(handler);
        var provider = new HomeAssistantProvider(http, Config(publicOrigin: origin), new TestEnvironment { EnvironmentName = "Production" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ReadAsync(origin, Tokens));
        Assert.Empty(handler.Paths);
    }

    [Fact]
    public async Task OtherAllowedHomesKeepTheirOwnOrigin()
    {
        var handler = new Handler();
        using var http = new HttpClient(handler);
        var provider = new HomeAssistantProvider(http, Config(), new TestEnvironment());
        await provider.ReadAsync("https://other.example.com", Tokens);
        Assert.Equal("https://other.example.com", Assert.Single(handler.Origins));
    }

    private static IConfiguration Config(string target = "http://homeassistant:8123", string publicOrigin = PublicOrigin) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["HomeAssistant:AllowedOrigins:0"] = publicOrigin,
            ["HomeAssistant:AllowedOrigins:1"] = "https://other.example.com",
            ["HomeAssistant:Transport:PublicOrigin"] = publicOrigin,
            ["HomeAssistant:Transport:InternalOrigin"] = target
        }).Build();

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = "/tmp";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class Handler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public List<string> Origins { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            Origins.Add(request.RequestUri.GetLeftPart(UriPartial.Authority));
            var body = request.RequestUri.AbsolutePath == "/auth/token"
                ? """{"access_token":"access","refresh_token":"refresh","expires_in":3600}"""
                : "[]";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}

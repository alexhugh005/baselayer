using System.Net;
using BaseLayer.Api.Providers;
using BaseLayer.Application.Interfaces;
using BaseLayer.Data;
using BaseLayer.Data.Repositories;
using BaseLayer.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;
namespace BaseLayer.Tests;

public sealed class PanelBenchBatteryTests
{
    [Theory]
    [InlineData("http://localhost:8123", "{\"connected\":true,\"battery\":{\"capacity_kwh\":13.5,\"stored_energy_kwh\":7.25}}", true, 1)]
    [InlineData("https://another-home.example", "{}", false, 0)]
    [InlineData("http://localhost:8123", "{\"connected\":false,\"battery\":null}", false, 1)]
    [InlineData("http://localhost:8123", "{\"connected\":true,\"battery\":{}}", false, 1)]
    [InlineData("http://localhost:8123", "not json", false, 1)]
    public async Task ReadsOnlyConfiguredLabAndNeverFallsBackToInventedCharge(string origin, string body, bool succeeds, int requests)
    {
        using var db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlite("Data Source=:memory:").Options);
        db.Database.OpenConnection();
        db.Database.EnsureCreated();
        var home = new Home { OwnerId = "alice", BaseUrl = origin };
        db.Homes.Add(home);
        await db.SaveChangesAsync();
        var handler = new Handler(body);
        using var http = new HttpClient(handler);
        var provider = new PanelBenchBatteryProvider(http, new PlatformRepository(db, new DatabaseGate()),
            new ConfigurationBuilder().Build(), TimeProvider.System);
        if (succeeds)
        {
            var result = await provider.ReadAsync(home.Id);
            Assert.Equal(13.5, result.CapacityKwh);
            Assert.Equal(7.25, result.StoredEnergyKwh);
            Assert.True(result.IsSimulated);
        }
        else await Assert.ThrowsAsync<BatteryProviderException>(() => provider.ReadAsync(home.Id));
        Assert.Equal(requests, handler.Calls);
    }

    [Fact]
    public async Task ReadsConfiguredContainerHealthEndpoint()
    {
        using var db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlite("Data Source=:memory:").Options);
        db.Database.OpenConnection();
        db.Database.EnsureCreated();
        var home = new Home { OwnerId = "alice", BaseUrl = "http://localhost:8123" };
        db.Homes.Add(home);
        await db.SaveChangesAsync();
        const string url = "http://panelbench:18081/health";
        var handler = new Handler("""{"connected":true,"battery":{"capacity_kwh":13.5,"stored_energy_kwh":7.25}}""", url);
        using var http = new HttpClient(handler);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Battery:PanelBench:HealthUrl"] = url
        }).Build();
        var provider = new PanelBenchBatteryProvider(http, new PlatformRepository(db, new DatabaseGate()), config, TimeProvider.System);
        Assert.Equal(7.25, (await provider.ReadAsync(home.Id)).StoredEnergyKwh);
        Assert.Equal(1, handler.Calls);
    }

    private sealed class Handler(string body, string expectedUrl = "http://127.0.0.1:18081/health") : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal(expectedUrl, request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}

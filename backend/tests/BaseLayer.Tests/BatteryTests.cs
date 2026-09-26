using System.Security.Claims;
using BaseLayer.Api.Controllers;
using BaseLayer.Api.Middleware;
using BaseLayer.Api.Providers;
using BaseLayer.Application.Contracts;
using BaseLayer.Application.Interfaces;
using BaseLayer.Application.Services;
using BaseLayer.Data;
using BaseLayer.Data.Repositories;
using BaseLayer.Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
namespace BaseLayer.Tests;

public sealed class BatteryTests : IDisposable
{
    private readonly PlatformDbContext db;
    private readonly BatteryClock clock = new();
    private readonly Guid homeId = Guid.NewGuid();

    public BatteryTests()
    {
        db = new(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlite("Data Source=:memory:").Options);
        db.Database.OpenConnection();
        db.Database.EnsureCreated();
        db.Homes.Add(new Home { Id = homeId, OwnerId = "alice" });
        db.SaveChanges();
    }

    private BatteryService Service(IBatteryProvider provider) => new(new PlatformRepository(db, new DatabaseGate()), provider);
    private SimulatedBatteryProvider Simulator(double watts = 1000, double percent = 80) =>
        new(Options.Create(new SimulatedBatteryOptions { CapacityKwh = 10, InitialStateOfChargePercent = percent, NetPowerWatts = watts }), clock);

    [Fact]
    public async Task SimulationUsesElapsedTimeAndStartsEachHomeIndependently()
    {
        var provider = Simulator();
        var service = Service(provider);
        var initial = await service.GetCurrentAsync("alice", homeId);
        Assert.Equal(10, initial.CapacityKwh);
        Assert.Equal(8, initial.StoredEnergyKwh);
        Assert.Equal(80, initial.StateOfChargePercent);
        Assert.True(initial.IsSimulated);
        Assert.Equal(homeId, initial.HomeId);

        clock.Advance(TimeSpan.FromHours(2));
        var current = await service.GetCurrentAsync("alice", homeId);
        Assert.Equal(6, current.StoredEnergyKwh);
        Assert.Equal(60, current.StateOfChargePercent);
        Assert.Equal(clock.GetUtcNow(), current.ObservedAtUtc);
        Assert.Equal(current, await service.GetCurrentAsync("alice", homeId));
        Assert.Equal(8, (await provider.ReadAsync(Guid.NewGuid())).StoredEnergyKwh);
    }

    [Theory]
    [InlineData(1000, 80, 0)]
    [InlineData(-1000, 80, 10)]
    [InlineData(0, 80, 8)]
    [InlineData(1000, 0, 0)]
    [InlineData(-1000, 100, 10)]
    public async Task SimulationClampsAtEmptyAndFullAndSupportsHoldingCharge(double watts, double percent, double expected)
    {
        var provider = Simulator(watts, percent);
        await provider.ReadAsync(homeId);
        clock.Advance(TimeSpan.FromDays(10));
        Assert.Equal(expected, (await provider.ReadAsync(homeId)).StoredEnergyKwh);
    }

    [Fact]
    public async Task ConcurrentReadsDoNotConsumeExtraEnergy()
    {
        var provider = Simulator();
        var readings = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(() => provider.ReadAsync(homeId))));
        Assert.All(readings, reading => Assert.Equal(8, reading.StoredEnergyKwh));
    }

    [Fact]
    public async Task UnknownAndOtherOwnersHomesNeverReachTheProvider()
    {
        var provider = new StubBatteryProvider();
        var service = Service(provider);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.GetCurrentAsync("bob", homeId));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.GetCurrentAsync("alice", Guid.NewGuid()));
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task ExternalAdapterCanReplaceSimulationWithoutChangingTheService()
    {
        using var cts = new CancellationTokenSource();
        var provider = new StubBatteryProvider { Snapshot = new(20, 5, clock.GetUtcNow(), false) };
        var result = await Service(provider).GetCurrentAsync("alice", homeId, cts.Token);
        Assert.Equal(25, result.StateOfChargePercent);
        Assert.False(result.IsSimulated);
        Assert.Equal(homeId, provider.HomeId);
        Assert.Equal(cts.Token, provider.Token);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 0)]
    [InlineData(double.NaN, 1)]
    [InlineData(double.PositiveInfinity, 1)]
    [InlineData(10, double.NaN)]
    [InlineData(10, double.PositiveInfinity)]
    [InlineData(10, -1)]
    [InlineData(10, 11)]
    public async Task InvalidExternalTelemetryFailsInsteadOfReturningAFabricatedLevel(double capacity, double energy)
    {
        var provider = new StubBatteryProvider { Snapshot = new(capacity, energy, clock.GetUtcNow(), false) };
        await Assert.ThrowsAsync<BatteryProviderException>(() => Service(provider).GetCurrentAsync("alice", homeId));
    }

    [Fact]
    public async Task MissingObservationTimeIsRejected()
    {
        var provider = new StubBatteryProvider { Snapshot = new(10, 5, default, false) };
        await Assert.ThrowsAsync<BatteryProviderException>(() => Service(provider).GetCurrentAsync("alice", homeId));
    }

    [Fact]
    public async Task ProviderFailuresAreBatterySpecificAndDoNotFallBackToSimulation()
    {
        foreach (var failure in new Exception[] { new HttpRequestException("upstream failure"), new TaskCanceledException("timeout") })
        {
            var provider = new StubBatteryProvider { Failure = failure };
            var exception = await Assert.ThrowsAsync<BatteryProviderException>(() => Service(provider).GetCurrentAsync("alice", homeId));
            Assert.Same(failure, exception.InnerException);
        }
    }

    [Fact]
    public async Task CancellationDoesNotStartOrReadTheSimulation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var provider = Simulator();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ReadAsync(homeId, cts.Token));
        clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(8, (await provider.ReadAsync(homeId)).StoredEnergyKwh);
        var stub = new StubBatteryProvider();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(stub).GetCurrentAsync("alice", homeId, cts.Token));
        Assert.Equal(0, stub.Calls);
    }

    [Theory]
    [InlineData("CapacityKwh", "0")]
    [InlineData("CapacityKwh", "NaN")]
    [InlineData("InitialStateOfChargePercent", "101")]
    [InlineData("InitialStateOfChargePercent", "-1")]
    [InlineData("NetPowerWatts", "Infinity")]
    public void InvalidConfigurationFailsValidation(string property, string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"Battery:Simulation:{property}"] = value
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddBatteryServices(configuration);
        using var container = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => container.GetRequiredService<IBatteryProvider>());
    }

    [Fact]
    public async Task ControllerUsesAuthenticatedOwnerAndReturnsTelemetry()
    {
        var controller = new BatteryController(Service(Simulator()))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "alice")], "test"))
                }
            }
        };
        var response = await controller.Get(homeId, CancellationToken.None);
        Assert.Equal(80, Assert.IsType<BatteryStatusDto>(Assert.IsType<OkObjectResult>(response.Result).Value).StateOfChargePercent);
        controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => controller.Get(homeId, CancellationToken.None));
    }

    [Fact]
    public async Task BatteryFailureReturns502WithoutExposingVendorDetails()
    {
        var middleware = new ExceptionMiddleware(_ => throw new BatteryProviderException("private vendor details"), NullLogger<ExceptionMiddleware>.Instance);
        var context = new DefaultHttpContext();
        using var body = new MemoryStream();
        context.Response.Body = body;
        await middleware.InvokeAsync(context);
        Assert.Equal(502, context.Response.StatusCode);
        body.Position = 0;
        var response = await new StreamReader(body).ReadToEndAsync();
        Assert.Contains("Battery telemetry is unavailable", response);
        Assert.DoesNotContain("private vendor details", response);
        Assert.DoesNotContain("Home Assistant", response);
    }

    public void Dispose() => db.Dispose();

    private sealed class BatteryClock : TimeProvider
    {
        private TimeSpan elapsed;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => elapsed.Ticks;
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.Zero) + elapsed;
        public void Advance(TimeSpan duration) => elapsed += duration;
    }

    private sealed class StubBatteryProvider : IBatteryProvider
    {
        public BatterySnapshot Snapshot { get; init; } = new(10, 5, DateTimeOffset.UtcNow, false);
        public Exception? Failure { get; init; }
        public int Calls { get; private set; }
        public Guid HomeId { get; private set; }
        public CancellationToken Token { get; private set; }
        public Task<BatterySnapshot> ReadAsync(Guid homeId, CancellationToken cancellationToken = default)
        {
            Calls++;
            HomeId = homeId;
            Token = cancellationToken;
            return Failure is null ? Task.FromResult(Snapshot) : Task.FromException<BatterySnapshot>(Failure);
        }
    }
}

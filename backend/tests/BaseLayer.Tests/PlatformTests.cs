using BaseLayer.Application.Contracts;
using BaseLayer.Application.Interfaces;
using BaseLayer.Application.Services;
using BaseLayer.Data;
using BaseLayer.Data.Repositories;
using BaseLayer.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;
namespace BaseLayer.Tests;

public sealed partial class PlatformTests : IDisposable
{
    private readonly PlatformDbContext db;
    private readonly FakeProvider provider = new();
    private readonly FakeClock clock = new();
    private readonly FakeGridOutageDetection gridRisk = new();
    private readonly PlatformService service;
    private sealed class FakeGridOutageDetection : IGridOutageDetectionService
    {
        public string Risk { get; set; } = GridOutageRisk.Medium;
        public Task<string> GetRiskAsync(Guid homeId) => Task.FromResult(Risk);
    }
    public PlatformTests()
    {
        db = new(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlite("Data Source=:memory:").Options);
        db.Database.OpenConnection();
        db.Database.EnsureCreated();
        service = new(new PlatformRepository(db, new DatabaseGate()), provider, new PlainProtector(), clock, new HomeOperationGate(), new UsageLimitReachedService(), gridRisk);
    }
    private async Task<HomeDto> Connect()
    {
        var start = await service.StartAsync("alice", new("Test", "http://localhost:8123"));
        return await service.CompleteAsync("alice", new("code", start.State));
    }
    private Task<HomeDto> Allow(HomeDto home, bool future = false) => service.SettingsAsync("alice", home.Id, new(true, future, [], "sensor.total", new() { { "switch.dryer", "sensor.dryer" } }));
    [Fact]
    public async Task ThermostatLimitsDefaultPersistAndRejectInvalidRanges()
    {
        var home = await Connect();
        var entity = await db.Homes.Include(h => h.Devices).SingleAsync();
        entity.Devices.Add(new Device { HomeId = home.Id, EntityId = "climate.room", Name = "Room", Present = true, State = "cool" });
        await db.SaveChangesAsync();
        var initial = (await service.HomesAsync("alice"))[0].Devices.Single(d => d.EntityId == "climate.room");
        Assert.Equal(66, initial.ThermostatMinF);
        Assert.Equal(80, initial.ThermostatMaxF);
        var request = new HomeSettingsRequest(true, false, [], null, new(), ThermostatLimits: new() { ["climate.room"] = new(67, 79) });
        await service.SettingsAsync("alice", home.Id, request);
        db.ChangeTracker.Clear();
        var saved = (await service.HomesAsync("alice"))[0].Devices.Single(d => d.EntityId == "climate.room");
        Assert.Equal(67, saved.ThermostatMinF);
        Assert.Equal(79, saved.ThermostatMaxF);
        foreach (var range in new[] { new ThermostatLimits(80, 66), new ThermostatLimits(70, 70), new ThermostatLimits(double.NaN, 80) })
            await Assert.ThrowsAsync<ArgumentException>(() => service.SettingsAsync("alice", home.Id, request with { ThermostatLimits = new() { ["climate.room"] = range } }));
        await Assert.ThrowsAsync<ArgumentException>(() => service.TurnOffAsync("alice", home.Id, new(["climate.room"], "thermostat")));
        Assert.Empty((await service.HomesAsync("alice"))[0].Commands);
    }
    [Fact]
    public async Task OAuthStateIsOwnerBoundAndSingleUse()
    {
        var start = await service.StartAsync("alice", new("Test", "http://localhost:8123"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CompleteAsync("bob", new("code", start.State)));
        await service.CompleteAsync("alice", new("code", start.State));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CompleteAsync("alice", new("code", start.State)));
        Assert.Equal(1, provider.Exchanges);
    }
    [Fact]
    public async Task OAuthStateExpires()
    {
        var start = await service.StartAsync("alice", new("Test", "http://localhost:8123"));
        clock.Advance(601);
        await Assert.ThrowsAsync<ArgumentException>(() => service.CompleteAsync("alice", new("code", start.State)));
        Assert.Equal(0, provider.Exchanges);
    }
    [Fact]
    public async Task TenantCannotReadChangeOrControlAnotherHome()
    {
        var home = await Connect();
        Assert.Empty(await service.HomesAsync("bob"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.SettingsAsync("bob", home.Id, new(true, true, [], null, new())));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.TurnOffAsync("bob", home.Id, new(["switch.dryer"], "x")));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.RevokeAsync("bob", home.Id));
    }
    [Fact]
    public async Task AllowAllCurrentDoesNotAllowNewDevicesUnlessOptedIn()
    {
        var home = await Connect();
        Assert.All(home.Devices, d => Assert.False(d.Allowed));
        await Allow(home);
        provider.Extra = true;
        await service.PollAsync(home.Id);
        home = (await service.HomesAsync("alice"))[0];
        Assert.True(home.Devices.Single(d => d.EntityId == "switch.dryer").Allowed);
        Assert.False(home.Devices.Single(d => d.EntityId == "switch.lamp").Allowed);
        await service.SettingsAsync("alice", home.Id, new(false, true, ["switch.dryer"], null, new()));
        provider.Another = true;
        await service.PollAsync(home.Id);
        Assert.True((await service.HomesAsync("alice"))[0].Devices.Single(d => d.EntityId == "switch.new").Allowed);
    }
    [Fact]
    public async Task ServiceSuccessRequiresObservedOffAndCommandsAreIdempotent()
    {
        var home = await Connect();
        await Allow(home);
        await service.PollAsync(home.Id);
        var command = await service.TurnOffAsync("alice", home.Id, new(["switch.dryer"], "same"));
        Assert.Equal(command[0].Id, (await service.TurnOffAsync("alice", home.Id, new(["switch.dryer"], "same")))[0].Id);
        await service.PollAsync(home.Id);
        Assert.Equal("AwaitingConfirmation", (await service.HomesAsync("alice"))[0].Commands[0].Status);
        provider.Off = true;
        await service.PollAsync(home.Id);
        Assert.Equal("Confirmed", (await service.HomesAsync("alice"))[0].Commands[0].Status);
        Assert.Equal(1, provider.Sends);
    }
    [Fact]
    public async Task RetryBackoffIsBoundedAndExpires()
    {
        var home = await Connect();
        await Allow(home);
        await service.TurnOffAsync("alice", home.Id, new(["switch.dryer"], "retry"));
        provider.Fail = true;
        await service.PollAsync(home.Id);
        await service.PollAsync(home.Id);
        Assert.Equal(1, provider.Sends);
        clock.Advance(5);
        await service.PollAsync(home.Id);
        Assert.Equal(2, provider.Sends);
        clock.Advance(10);
        await service.PollAsync(home.Id);
        clock.Advance(20);
        await service.PollAsync(home.Id);
        Assert.Equal("Failed", (await service.HomesAsync("alice"))[0].Commands[0].Status);
        Assert.Equal(4, provider.Sends);
        await service.TurnOffAsync("alice", home.Id, new(["switch.dryer"], "expire"));
        clock.Advance(121);
        await service.PollAsync(home.Id);
        Assert.Equal("Expired", (await service.HomesAsync("alice"))[0].Commands[0].Status);
        Assert.Equal(4, provider.Sends);
    }
    [Fact]
    public async Task MissingMeterIsUnknownAndHighUsageNeverAutomaticallySends()
    {
        var home = await Connect();
        Assert.Null(home.HouseholdWatts);
        await Allow(home);
        await service.PollAsync(home.Id);
        home = (await service.HomesAsync("alice"))[0];
        Assert.Equal(13000, home.HouseholdWatts);
        Assert.True(home.Devices[0].Recommended);
        Assert.Equal(0, provider.Sends);
    }
    [Fact]
    public async Task ReachedLimitDelegatesRecommendationsAndOfflineReadingsDoNot()
    {
        var home = await Connect();
        await Allow(home);
        await service.PollAsync(home.Id);
        var recommender = new RecordingUsageLimitService();
        var platform = new PlatformService(new PlatformRepository(db, new DatabaseGate()), provider,
            new PlainProtector(), clock, new HomeOperationGate(), recommender, gridRisk);
        var result = (await platform.HomesAsync("alice"))[0];
        Assert.Equal(1, recommender.Calls);
        Assert.Equal(13000, recommender.CurrentWatts);
        Assert.Equal(11000, recommender.LimitWatts);
        Assert.True(result.Devices.Single().Recommended);
        Assert.Equal(8000, result.ProjectedWatts);
        Assert.Equal(0, provider.Sends);

        var entity = await db.Homes.SingleAsync();
        entity.HouseholdWatts = 10999;
        result = (await platform.HomesAsync("alice"))[0];
        Assert.Equal(1, recommender.Calls);
        Assert.False(result.Devices.Single().Recommended);
        Assert.Equal(10999, result.ProjectedWatts);

        entity.HouseholdWatts = 11000;
        await platform.HomesAsync("alice");
        Assert.Equal(2, recommender.Calls);
        clock.Advance(21);
        result = (await platform.HomesAsync("alice"))[0];
        Assert.Equal(2, recommender.Calls);
        Assert.False(result.Devices.Single().Recommended);
        Assert.Null(result.ProjectedWatts);
    }
    private sealed class RecordingUsageLimitService : IUsageLimitReachedService
    {
        public int Calls;
        public double CurrentWatts, LimitWatts;
        public IReadOnlyList<TurnOffRecommendation> RecommendActions(double currentWatts, double limitWatts, IEnumerable<Device> devices)
        {
            Calls++;
            CurrentWatts = currentWatts;
            LimitWatts = limitWatts;
            return [new("switch.dryer", 5000)];
        }
    }
    [Fact]
    public async Task PermissionRemovalCancelsPendingCommand()
    {
        var home = await Connect();
        await Allow(home);
        await service.TurnOffAsync("alice", home.Id, new(["switch.dryer"], "cancel"));
        await service.SettingsAsync("alice", home.Id, new(false, false, [], null, new()));
        await service.PollAsync(home.Id);
        Assert.Equal("Cancelled", (await service.HomesAsync("alice"))[0].Commands[0].Status);
        Assert.Equal(0, provider.Sends);
    }
    [Fact]
    public async Task HouseholdMeterCannotBeUsedAsDeviceMeter()
    {
        var home = await Connect();
        await Assert.ThrowsAsync<ArgumentException>(() => service.SettingsAsync("alice", home.Id,
            new(true, false, [], "sensor.total", new() { { "switch.dryer", "sensor.total" } })));
    }
    [Fact]
    public async Task DeviceSumIncludesOffAndUnapprovedDevicesAndClearsHouseholdMeter()
    {
        provider.Snapshot = new([new("switch.dryer", "Dryer", "on"), new("switch.lamp", "Lamp", "off")],
            [new("sensor.dryer", "Dryer", "W"), new("sensor.lamp", "Lamp", "W")],
            new()
            {
                ["sensor.dryer"] = 5000,
                ["sensor.lamp"] = 8
            });
        var home = await Connect();
        await service.SettingsAsync("alice", home.Id, new(false, false, [], "sensor.old_meter",
            new()
            {
                ["switch.dryer"] = "sensor.dryer",
                ["switch.lamp"] = "sensor.lamp"
            }, "deviceSum"));
        await service.PollAsync(home.Id);
        home = (await service.HomesAsync("alice"))[0];
        Assert.Equal("deviceSum", home.PowerSource);
        Assert.Null(home.HouseholdPowerSensorId);
        Assert.Equal(5008, home.HouseholdWatts);
        Assert.All(home.Devices, device => Assert.False(device.Allowed));
        Assert.Equal(0, provider.Sends);
    }
    [Theory]
    [InlineData("noMappings")]
    [InlineData("missingSensor")]
    [InlineData("unavailableSensor")]
    [InlineData("missingDevice")]
    public async Task DeviceSumIsUnknownWhenConfiguredCoverageIsMissing(string scenario)
    {
        var home = await Connect();
        await service.SettingsAsync("alice", home.Id, new(false, false, [], null,
            scenario == "noMappings" ? new() : new()
            {
                ["switch.dryer"] = "sensor.dryer"
            }, "deviceSum"));
        provider.Snapshot = new(scenario == "missingDevice" ? [] : [new("switch.dryer", "Dryer", "on")],
            [new("sensor.dryer", "Dryer", "W")],
            scenario == "missingSensor" ? new() : new()
            {
                ["sensor.dryer"] = scenario == "unavailableSensor" ? null : 5000
            });
        await service.PollAsync(home.Id);
        Assert.Null((await service.HomesAsync("alice"))[0].HouseholdWatts);
    }
    [Fact]
    public async Task DeviceSumRejectsDuplicateSensorsAndUnknownModes()
    {
        provider.Extra = true;
        var home = await Connect();
        await Assert.ThrowsAsync<ArgumentException>(() => service.SettingsAsync("alice", home.Id,
            new(false, false, [], null, new()
            {
                ["switch.dryer"] = "sensor.dryer",
                ["switch.lamp"] = "sensor.dryer"
            }, "deviceSum")));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SettingsAsync("alice", home.Id,
            new(false, false, [], null, new(), "badSource")));
    }
    [Fact]
    public async Task SourcePersistsAcrossScopesAndOldRequestsStillUseMeter()
    {
        var home = await Connect();
        await service.SettingsAsync("alice", home.Id, new(false, false, [], null,
            new()
            {
                ["switch.dryer"] = "sensor.dryer"
            }, "deviceSum"));
        using (var persisted = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlite(db.Database.GetDbConnection()).Options))
            Assert.Equal("deviceSum", (await persisted.Homes.SingleAsync()).PowerSource);
        await Allow(home);
        await service.PollAsync(home.Id);
        home = (await service.HomesAsync("alice"))[0];
        Assert.Equal("wholeHouseMeter", home.PowerSource);
        Assert.Equal(13000, home.HouseholdWatts);
        var oldRequest = System.Text.Json.JsonSerializer.Deserialize<HomeSettingsRequest>("""
            {"AllowAll":false,"AllowFutureDevices":false,"AllowedEntityIds":[],"HouseholdPowerSensorId":null,"DevicePowerSensors":{}}
            """);
        Assert.Equal("wholeHouseMeter", oldRequest!.PowerSource);
    }
    [Fact]
    public async Task ExistingDatabaseIsUpgradedIdempotentlyWithoutLosingConnectionOrCommands()
    {
        var home = await Connect();
        await Allow(home);
        await service.TurnOffAsync("alice", home.Id, new(["switch.dryer"], "schema-upgrade"));
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE Homes DROP COLUMN PowerSource");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE Device DROP COLUMN ThermostatMinF");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE Device DROP COLUMN ThermostatMaxF");
        db.ChangeTracker.Clear();
        await DatabaseInitializer.InitializeAsync(db);
        await DatabaseInitializer.InitializeAsync(db);
        var preserved = await db.Homes.Include(h => h.Devices).Include(h => h.Commands).SingleAsync();
        Assert.Equal("wholeHouseMeter", preserved.PowerSource);
        Assert.Equal("alice", preserved.OwnerId);
        Assert.NotNull(preserved.ProtectedTokens);
        Assert.Equal("sensor.total", preserved.HouseholdPowerSensorId);
        Assert.Equal(66, preserved.Devices.Single().ThermostatMinF);
        Assert.Equal(80, preserved.Devices.Single().ThermostatMaxF);
        Assert.True(preserved.Devices.Single().Allowed);
        Assert.Equal("sensor.dryer", preserved.Devices.Single().PowerSensorId);
        Assert.Equal("schema-upgrade", preserved.Commands.Single().IdempotencyKey);
    }
    [Theory]
    [InlineData("Pending")]
    [InlineData("Retrying")]
    [InlineData("AwaitingConfirmation")]
    public async Task UserCanCancelOutstandingWorkWithoutFurtherAttempts(string status)
    {
        var home = await Connect();
        await Allow(home);
        var command = (await service.TurnOffAsync("alice", home.Id, new(["switch.dryer"], "cancel-user")))[0];
        if (status != "Pending")
        {
            provider.Fail = status == "Retrying";
            await service.PollAsync(home.Id);
        }
        Assert.Equal(status, (await service.HomesAsync("alice"))[0].Commands.Single().Status);
        var sendsBeforeCancel = provider.Sends;
        var cancelled = await service.CancelCommandAsync("alice", home.Id, command.Id);
        Assert.Equal("Cancelled", cancelled.Status);
        Assert.Contains("already sent cannot be undone", cancelled.Message);
        Assert.Equal(cancelled, await service.CancelCommandAsync("alice", home.Id, command.Id));
        clock.Advance(30);
        await service.PollAsync(home.Id);
        Assert.Equal(sendsBeforeCancel, provider.Sends);
        using var persisted = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlite(db.Database.GetDbConnection()).Options);
        Assert.Equal("Cancelled", (await persisted.Set<DeviceCommand>().SingleAsync()).Status);
    }
    [Theory]
    [InlineData("Confirmed")]
    [InlineData("Failed")]
    [InlineData("Expired")]
    [InlineData("Cancelled")]
    public async Task CancelPreservesTerminalCommandHistory(string status)
    {
        var home = await Connect();
        await Allow(home);
        var command = (await service.TurnOffAsync("alice", home.Id, new(["switch.dryer"], "terminal")))[0];
        var record = await db.Set<DeviceCommand>().SingleAsync();
        record.Status = status;
        record.Message = "Existing outcome";
        record.Attempts = 2;
        await db.SaveChangesAsync();
        var result = await service.CancelCommandAsync("alice", home.Id, command.Id);
        Assert.Equal(status, result.Status);
        Assert.Equal("Existing outcome", result.Message);
        Assert.Equal(2, result.Attempts);
    }
    [Fact]
    public async Task CancelAndDeleteAreBoundToOwnerAndCommandHome()
    {
        var home = await Connect();
        await Allow(home);
        var command = (await service.TurnOffAsync("alice", home.Id, new(["switch.dryer"], "owned")))[0];
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.CancelCommandAsync("bob", home.Id, command.Id));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.DeleteAsync("bob", home.Id));
        var another = await Connect();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.CancelCommandAsync("alice", another.Id, command.Id));
        Assert.Equal("Pending", (await service.HomesAsync("alice")).Single(h => h.Id == home.Id).Commands.Single().Status);
        Assert.Equal(0, provider.Revokes);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteCascadesHistoryAndDevicesEvenWhenRevokedOrRemoteUnavailable(bool revoked)
    {
        var home = await Connect();
        await Allow(home);
        await service.TurnOffAsync("alice", home.Id, new(["switch.dryer"], "delete-home"));
        if (revoked)
            await service.RevokeAsync("alice", home.Id);
        provider.FailRevoke = true;
        await service.DeleteAsync("alice", home.Id);
        await service.PollAsync(home.Id);
        Assert.Empty(await service.HomesAsync("alice"));
        Assert.Equal(0, provider.Sends);
        Assert.Equal(1, provider.Revokes);
        using var persisted = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlite(db.Database.GetDbConnection()).Options);
        Assert.Empty(await persisted.Homes.ToListAsync());
        Assert.Empty(await persisted.Set<Device>().ToListAsync());
        Assert.Empty(await persisted.Set<DeviceCommand>().ToListAsync());
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.DeleteAsync("alice", home.Id));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelOrDeleteSerializesWithAnInFlightPoll(bool delete)
    {
        var home = await Connect();
        await Allow(home);
        var command = (await service.TurnOffAsync("alice", home.Id, new(["switch.dryer"], "race")))[0];
        provider.SendStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.ReleaseSend = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var poll = service.PollAsync(home.Id);
        await provider.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task change = delete ? service.DeleteAsync("alice", home.Id) : service.CancelCommandAsync("alice", home.Id, command.Id);
        Assert.False(change.IsCompleted);
        provider.ReleaseSend.SetResult();
        await Task.WhenAll(poll, change);
        clock.Advance(30);
        await service.PollAsync(home.Id);
        Assert.Equal(1, provider.Sends);
        if (delete)
            Assert.Empty(await service.HomesAsync("alice"));
        else
            Assert.Equal("Cancelled", (await service.HomesAsync("alice"))[0].Commands.Single().Status);
    }
    [Fact]
    public async Task LastAttemptCanConfirmAfterItsObservationLease()
    {
        var home = await Connect();
        await Allow(home);
        await service.TurnOffAsync("alice", home.Id, new(["switch.dryer"], "last"));
        await service.PollAsync(home.Id);
        for (var i = 0; i < 3; i++)
        {
            clock.Advance(11);
            await service.PollAsync(home.Id);
            clock.Advance(5 * (1 << i));
            await service.PollAsync(home.Id);
        }
        Assert.Equal(4, provider.Sends);
        provider.Off = true;
        clock.Advance(11);
        await service.PollAsync(home.Id);
        Assert.Equal("Confirmed", (await service.HomesAsync("alice"))[0].Commands[0].Status);
    }
    [Fact]
    public async Task ScopedHttpRequestsCanAddCommandsAndNewDevices()
    {
        var home = await Connect();
        await Allow(home);
        var gate = new HomeOperationGate();
        var databaseGate = new DatabaseGate();
        PlatformDbContext NewContext() => new(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(db.Database.GetDbConnection()).Options);
        PlatformService NewService(PlatformDbContext context) => new(new PlatformRepository(context, databaseGate),
            provider, new PlainProtector(), clock, gate, new UsageLimitReachedService(), gridRisk);
        using (var request = NewContext())
        {
            var commands = await NewService(request).TurnOffAsync("alice", home.Id, new(["switch.dryer"], "scoped"));
            Assert.Single(commands);
        }
        using (var worker = NewContext())
        {
            provider.Extra = true;
            await NewService(worker).PollAsync(home.Id);
        }
        using (var request = NewContext())
        {
            var result = (await NewService(request).HomesAsync("alice"))[0];
            Assert.Equal("AwaitingConfirmation", result.Commands[0].Status);
            Assert.Equal(2, result.Devices.Count);
        }
    }
    public void Dispose() => db.Dispose();
    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => now; public void Advance(int seconds) => now = now.AddSeconds(seconds);
    }
    private sealed class PlainProtector : ICredentialProtector
    {
        public string Protect(ProviderTokens t) => System.Text.Json.JsonSerializer.Serialize(t); public ProviderTokens Unprotect(string s) => System.Text.Json.JsonSerializer.Deserialize<ProviderTokens>(s)!;
    }
    private sealed class FakeProvider : ISmartHomeProvider
    {
        public ProviderSnapshot? Snapshot;
        public int Reads, Refreshes, UnauthorizedReads;
        public bool FailRefresh;
        public ProviderTokens? LastReadTokens;
        public bool Extra, Another, Off, Fail, FailRevoke, FailRead; public int Exchanges, Sends, Revokes, TurnOns;
        public TaskCompletionSource? SendStarted, ReleaseSend;
        public string ValidateOrigin(string s) => s; public string AuthorizationUrl(string o, string s) => o + "/auth?state=" + s;
        public Task<ProviderTokens> ExchangeAsync(string o, string c)
        {
            Exchanges++;
            return Task.FromResult(new ProviderTokens("access", "refresh", DateTime.UtcNow.AddHours(1)));
        }
        public Task<ProviderTokens> RefreshAsync(string o, ProviderTokens t)
        {
            Refreshes++;
            if (FailRefresh) throw new HttpRequestException("Refresh rejected", null, System.Net.HttpStatusCode.BadRequest);
            return Task.FromResult(t with { AccessToken = "renewed", ExpiresUtc = DateTime.UtcNow.AddHours(1) });
        }
        public Task<ProviderSnapshot> ReadAsync(string o, ProviderTokens t)
        {
            Reads++;
            LastReadTokens = t;
            if (UnauthorizedReads > 0)
            {
                UnauthorizedReads--;
                throw new HttpRequestException("Token rejected", null, System.Net.HttpStatusCode.Unauthorized);
            }
            if (FailRead) throw new HttpRequestException();
            if (Snapshot is not null)
                return Task.FromResult(Snapshot);
            List<ProviderDevice> devices = [new("switch.dryer", "Dryer", Off ? "off" : "on")];
            if (Extra)
                devices.Add(new("switch.lamp", "Lamp", "on"));
            if (Another)
                devices.Add(new("switch.new", "New", "on"));
            return Task.FromResult(new ProviderSnapshot(devices, [new("sensor.total", "Total", "W"), new("sensor.dryer", "Dryer", "W")], new() { { "sensor.total", 13000 }, { "sensor.dryer", 5000 } }));
        }
        public async Task TurnOffAsync(string o, ProviderTokens t, string id)
        {
            Sends++;
            SendStarted?.TrySetResult();
            if (ReleaseSend is not null)
                await ReleaseSend.Task;
            if (Fail)
                throw new HttpRequestException();
        }
        public List<(string EntityId, string Priority)> PrioritySends = [];
        public Task SetCircuitPriorityAsync(string o, ProviderTokens t, string id, string priority)
        {
            PrioritySends.Add((id, priority));
            if (Fail) throw new HttpRequestException();
            return Task.CompletedTask;
        }
        public List<(string EntityId, double Percent)> ChargeLimitSends = [];
        public Task SetChargeLimitAsync(string o, ProviderTokens t, string id, double percent)
        {
            ChargeLimitSends.Add((id, percent));
            if (Fail) throw new HttpRequestException();
            return Task.CompletedTask;
        }
        public List<(string EntityId, double Amps)> CurrentSends = [];
        public Task SetCurrentAsync(string o, ProviderTokens t, string id, double amps)
        {
            CurrentSends.Add((id, amps));
            if (Fail) throw new HttpRequestException();
            return Task.CompletedTask;
        }
        public Task TurnOnAsync(string o, ProviderTokens t, string id)
        {
            TurnOns++;
            if (Fail) throw new HttpRequestException();
            return Task.CompletedTask;
        }
        public List<(string EntityId, string State)> DeviceRestores = [];
        public Task RestoreDeviceStateAsync(string o, ProviderTokens t, string id, string state)
        {
            DeviceRestores.Add((id, state));
            return TurnOnAsync(o, t, id);
        }
        public Task RevokeAsync(string o, ProviderTokens t)
        {
            Revokes++;
            if (FailRevoke)
                throw new HttpRequestException();
            return Task.CompletedTask;
        }
    }
}

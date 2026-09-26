using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BaseLayer.Application.Contracts;
using BaseLayer.Application.Interfaces;
using BaseLayer.Domain.Entities;
namespace BaseLayer.Application.Services;

public sealed class PlatformService(IPlatformRepository repository, ISmartHomeProvider provider, ICredentialProtector protector, TimeProvider clock, HomeOperationGate operations) : IPlatformService
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool Fresh(Home home, DateTime now) => !home.Revoked && home.LastSeenUtc >= now.AddSeconds(-20);
    private static bool Active(Device d) => d.Present && d.State is not ("off" or "unknown" or "unavailable");
    private async Task<Home> Owned(string owner, Guid id)
    {
        var home = await repository.HomeAsync(id);
        return home is not null && home.OwnerId == owner ? home : throw new KeyNotFoundException("Home not found.");
    }
    private static List<PowerSensorDto> Sensors(Home home) => JsonSerializer.Deserialize<List<PowerSensorDto>>(home.SensorsJson) ?? [];
    private HomeDto ToDto(Home home)
    {
        var online = Fresh(home, Now);
        var recommendations = new HashSet<string>();
        var projected = online ? home.HouseholdWatts : null;
        if (projected >= 11000)
            foreach (var device in home.Devices.Where(d => d.Allowed && Active(d) && d.PowerWatts > 0).OrderByDescending(d => d.PowerWatts))
            {
                recommendations.Add(device.EntityId);
                projected = Math.Max(0, projected.Value - device.PowerWatts!.Value);
                if (projected < 11000)
                    break;
            }
        return new(home.Id, home.Name, online, home.Revoked, home.LastSeenUtc, online ? home.HouseholdWatts : null, 11000, projected,
            home.Devices.Where(d => d.Present).OrderBy(d => d.Name).Select(d => new DeviceDto(d.EntityId, d.Name, online ? d.State : "unavailable", online ? d.PowerWatts : null, d.Allowed, recommendations.Contains(d.EntityId), d.PowerSensorId)).ToList(),
            home.Commands.OrderByDescending(c => c.CreatedUtc).Take(30).Select(Dto).ToList(), home.BaseUrl, home.HouseholdPowerSensorId, home.AllowFutureDevices, Sensors(home), home.PowerSource);
    }
    private static CommandDto Dto(DeviceCommand c) => new(c.Id, c.EntityId, c.Status, c.Attempts, c.CreatedUtc, c.Message);
    public async Task<List<HomeDto>> HomesAsync(string owner) => (await repository.HomesAsync(owner)).Select(ToDto).ToList();
    public Task<OAuthStartResult> StartAsync(string owner, OAuthStartRequest request) => repository.TransactionAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 80)
            throw new ArgumentException("Enter a home name up to 80 characters.");
        if ((await repository.HomesAsync(owner)).Count >= 20)
            throw new ArgumentException("Maximum 20 homes reached.");
        var origin = provider.ValidateOrigin(request.BaseUrl);
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var pending = new OAuthState { OwnerId = owner, Name = request.Name.Trim(), BaseUrl = origin, CodeHash = Hash(state), ExpiresUtc = Now.AddMinutes(10) };
        repository.Add(pending);
        await repository.SaveAsync();
        return new OAuthStartResult(provider.AuthorizationUrl(origin, state), state, pending.ExpiresUtc);
    });
    public async Task<HomeDto> CompleteAsync(string owner, OAuthCompleteRequest request)
    {
        if (string.IsNullOrEmpty(request.State) || request.State.Length > 128 || string.IsNullOrEmpty(request.Code) || request.Code.Length > 2048)
            throw new ArgumentException("Invalid authorization response.");
        var pending = await repository.TransactionAsync(async () =>
        {
            var state = await repository.StateAsync(Hash(request.State));
            if (state is null || state.OwnerId != owner || state.Used || state.ExpiresUtc <= Now)
                throw new ArgumentException("Authorization expired, belongs to another user, or was already used. Connect again.");
            state.Used = true;
            await repository.SaveAsync();
            return state;
        });
        var tokens = await provider.ExchangeAsync(pending.BaseUrl, request.Code);
        var snapshot = await provider.ReadAsync(pending.BaseUrl, tokens);
        return await repository.TransactionAsync(async () =>
        {
            var home = new Home { OwnerId = owner, Name = pending.Name, BaseUrl = pending.BaseUrl, ProtectedTokens = protector.Protect(tokens) };
            ApplySnapshot(home, snapshot);
            repository.Add(home);
            await repository.SaveAsync();
            return ToDto(home);
        });
    }
    public Task<HomeDto> SettingsAsync(string owner, Guid id, HomeSettingsRequest request) => operations.RunAsync(id, () => repository.TransactionAsync(async () =>
    {
        var home = await Owned(owner, id);
        if (home.Revoked)
            throw new ArgumentException("Connection revoked.");
        if (request.PowerSource is not (HouseholdPowerSources.WholeHouseMeter or HouseholdPowerSources.DeviceSum))
            throw new ArgumentException("Choose a whole-house meter or the sum of device power readings.");
        var householdMeter = request.PowerSource == HouseholdPowerSources.WholeHouseMeter ? request.HouseholdPowerSensorId : null;
        var sensors = Sensors(home).Select(s => s.EntityId).ToHashSet();
        var devices = home.Devices.Where(d => d.Present).Select(d => d.EntityId).ToHashSet();
        if (request.AllowedEntityIds.Any(id => !devices.Contains(id)) || request.DevicePowerSensors.Any(kv => !devices.Contains(kv.Key) || (kv.Value is not null && !sensors.Contains(kv.Value))) || (householdMeter is not null && !sensors.Contains(householdMeter)))
            throw new ArgumentException("Choose devices and power sensors from this home's discovered entities.");
        var mapped = request.DevicePowerSensors.Values.Where(v => v is not null).ToList();
        if (householdMeter is not null && mapped.Contains(householdMeter))
            throw new ArgumentException("The household meter cannot also be mapped to an individual device.");
        if (mapped.Distinct().Count() != mapped.Count)
            throw new ArgumentException("Map a power sensor to only one device to avoid double-counting.");
        home.AllowFutureDevices = request.AllowFutureDevices;
        home.PowerSource = request.PowerSource;
        home.HouseholdPowerSensorId = householdMeter;
        home.HouseholdWatts = null;
        foreach (var device in home.Devices)
        {
            device.Allowed = device.Present && (request.AllowAll || request.AllowedEntityIds.Contains(device.EntityId));
            device.PowerSensorId = request.DevicePowerSensors.GetValueOrDefault(device.EntityId);
            device.PowerWatts = null;
        }
        foreach (var c in home.Commands.Where(c => !CommandPolicy.Terminal(c) && !home.Devices.Any(d => d.EntityId == c.EntityId && d.Allowed)))
        {
            c.Status = "Cancelled";
            c.Message = "Device access removed.";
        }
        await repository.SaveAsync();
        return ToDto(home);
    }));
    public Task<List<CommandDto>> TurnOffAsync(string owner, Guid id, TurnOffRequest request) => operations.RunAsync(id, () => repository.TransactionAsync(async () =>
    {
        var home = await Owned(owner, id);
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 100 || request.EntityIds.Count is < 1 or > 100)
            throw new ArgumentException("Invalid command request.");
        var prior = home.Commands.Where(c => c.IdempotencyKey == request.IdempotencyKey).ToList();
        if (prior.Count > 0)
        {
            if (!prior.Select(c => c.EntityId).ToHashSet().SetEquals(request.EntityIds))
                throw new ArgumentException("Idempotency key was used for different devices.");
            return prior.Select(Dto).ToList();
        }
        if (!Fresh(home, Now))
            throw new ArgumentException("Home is offline. Reconnect before controlling devices.");
        var ids = request.EntityIds.Distinct().ToList();
        foreach (var entityId in ids)
            if (!home.Devices.Any(d => d.EntityId == entityId && d.Allowed && Active(d)))
                throw new ArgumentException("Selected devices must be active and allowed.");
        foreach (var command in home.Commands)
            CommandPolicy.Advance(command, Now);
        if (home.Commands.Any(c => ids.Contains(c.EntityId) && !CommandPolicy.Terminal(c)))
            throw new ArgumentException("A command is already pending.");
        var commands = ids.Select(entityId => new DeviceCommand { HomeId = id, EntityId = entityId, RequestedBy = owner, IdempotencyKey = request.IdempotencyKey, CreatedUtc = Now, ExpiresUtc = Now.AddMinutes(2), NextAttemptUtc = Now }).ToList();
        home.Commands.AddRange(commands);
        await repository.SaveAsync();
        return commands.Select(Dto).ToList();
    }));
    public Task<CommandDto> CancelCommandAsync(string owner, Guid id, Guid commandId) => operations.RunAsync(id, () => repository.TransactionAsync(async () =>
    {
        var home = await Owned(owner, id);
        var command = home.Commands.SingleOrDefault(c => c.Id == commandId) ?? throw new KeyNotFoundException("Command not found.");
        if (!CommandPolicy.Terminal(command))
        {
            command.Status = "Cancelled";
            command.Message = "Future attempts cancelled by the user. Any command already sent cannot be undone.";
            command.LeaseUntilUtc = null;
            command.AttemptToken = null;
            await repository.SaveAsync();
        }
        return Dto(command);
    }));
    public Task DeleteAsync(string owner, Guid id) => operations.RunAsync(id, async () =>
    {
        var saved = await repository.TransactionAsync(async () =>
        {
            var home = await Owned(owner, id);
            var secret = home.ProtectedTokens;
            repository.Remove(home);
            await repository.SaveAsync();
            return (home.BaseUrl, secret);
        });
        // Local deletion commits first: no poll can retry commands even if HA is offline.
        await RevokeTokensBestEffortAsync(saved.BaseUrl, saved.secret);
    });
    private void ApplySnapshot(Home home, ProviderSnapshot snapshot)
    {
        home.LastSeenUtc = Now;
        home.SensorsJson = JsonSerializer.Serialize(snapshot.Sensors);
        foreach (var device in home.Devices)
            device.Present = false;
        foreach (var reading in snapshot.Devices)
        {
            var device = home.Devices.SingleOrDefault(d => d.EntityId == reading.EntityId);
            if (device is null)
            {
                device = new Device { HomeId = home.Id, EntityId = reading.EntityId, Allowed = home.AllowFutureDevices };
                home.Devices.Add(device);
            }
            device.Name = reading.Name;
            device.State = reading.State;
            device.Present = true;
            device.PowerWatts = device.PowerSensorId is { } sensor ? snapshot.Power.GetValueOrDefault(sensor) : null;
        }
        home.HouseholdWatts = home.PowerSource == HouseholdPowerSources.DeviceSum
            ? SumDevicePower(home, snapshot)
            : home.HouseholdPowerSensorId is { } meter ? snapshot.Power.GetValueOrDefault(meter) : null;
    }
    private static double? SumDevicePower(Home home, ProviderSnapshot snapshot)
    {
        var mapped = home.Devices.Where(d => d.PowerSensorId is not null).ToList();
        // A configured device disappearing must not make the displayed load silently shrink.
        if (mapped.Count == 0 || mapped.Any(d => !d.Present))
            return null;
        double total = 0;
        foreach (var sensor in mapped.Select(d => d.PowerSensorId!).Distinct())
        {
            if (!snapshot.Power.TryGetValue(sensor, out var watts) || watts is null || !double.IsFinite(watts.Value))
                return null;
            total += watts.Value;
        }
        return double.IsFinite(total) ? total : null;
    }
    public Task PollAsync(Guid id) => operations.RunAsync(id, () => PollCoreAsync(id));
    private async Task PollCoreAsync(Guid id)
    {
        var home = await repository.HomeAsync(id);
        if (home is null || home.Revoked || home.ProtectedTokens is null)
            return;
        foreach (var command in home.Commands.Where(c => c.ExpiresUtc <= Now))
            CommandPolicy.Advance(command, Now);
        await repository.SaveAsync();
        var tokens = protector.Unprotect(home.ProtectedTokens);
        if (tokens.ExpiresUtc <= Now.AddMinutes(1))
        {
            tokens = await provider.RefreshAsync(home.BaseUrl, tokens);
            home.ProtectedTokens = protector.Protect(tokens);
            await repository.SaveAsync();
        }
        ProviderSnapshot snapshot;
        try
        {
            snapshot = await provider.ReadAsync(home.BaseUrl, tokens);
        }
        catch { foreach (var c in home.Commands) CommandPolicy.Advance(c, Now); await repository.SaveAsync(); throw; }
        ApplySnapshot(home, snapshot);
        foreach (var command in home.Commands.Where(c => !CommandPolicy.Terminal(c)))
        {
            // Expiry is checked before observations: stale approvals never trigger another attempt.
            if (command.ExpiresUtc <= Now)
                CommandPolicy.Advance(command, Now);
            if (CommandPolicy.Terminal(command))
                continue;
            var device = home.Devices.SingleOrDefault(d => d.EntityId == command.EntityId && d.Present);
            if (device is null || !device.Allowed)
            {
                command.Status = "Cancelled";
                command.Message = "Device unavailable or access removed.";
                continue;
            }
            if (command.Attempts > 0 && device.State == "off")
            {
                command.Status = "Confirmed";
                command.Message = "Home Assistant state confirms the device is off.";
                continue;
            }
            CommandPolicy.Advance(command, Now);
            if (CommandPolicy.Terminal(command) || command.Status == "AwaitingConfirmation" || command.NextAttemptUtc > Now)
                continue;
            if (device.State == "off")
            {
                command.Status = "Confirmed";
                command.Message = "Device is already off.";
                continue;
            }
            if (!Active(device))
            {
                CommandPolicy.Retry(command, Now, "Device state is unavailable.");
                continue;
            }
            command.Attempts++;
            command.Status = "AwaitingConfirmation";
            command.LeaseUntilUtc = Now.AddSeconds(10);
            await repository.SaveAsync();
            try
            {
                await provider.TurnOffAsync(home.BaseUrl, tokens, command.EntityId);
            }
            catch (HttpRequestException) { CommandPolicy.Retry(command, Now, "Home Assistant rejected or could not receive the command."); }
            catch (TaskCanceledException) { CommandPolicy.Retry(command, Now, "Home Assistant command timed out."); }
        }
        await repository.SaveAsync();
    }
    public Task RevokeAsync(string owner, Guid id) => operations.RunAsync(id, () => RevokeCoreAsync(owner, id));
    private async Task RevokeCoreAsync(string owner, Guid id)
    {
        var saved = await repository.TransactionAsync(async () =>
        {
            var home = await Owned(owner, id);
            var secret = home.ProtectedTokens;
            home.ProtectedTokens = null;
            home.Revoked = true;
            foreach (var command in home.Commands.Where(c => !CommandPolicy.Terminal(c)))
            {
                command.Status = "Cancelled";
                command.Message = "Connection revoked.";
            }
            await repository.SaveAsync();
            return (home.BaseUrl, secret);
        });
        await RevokeTokensBestEffortAsync(saved.BaseUrl, saved.secret);
    }
    private async Task RevokeTokensBestEffortAsync(string baseUrl, string? protectedTokens)
    {
        if (protectedTokens is not null)
            try
            {
                await provider.RevokeAsync(baseUrl, protector.Unprotect(protectedTokens));
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            catch (CryptographicException) { }
            catch (JsonException) { }
    }
}

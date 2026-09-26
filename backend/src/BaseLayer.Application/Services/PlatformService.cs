using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BaseLayer.Application.Contracts;
using BaseLayer.Application.Interfaces;
using BaseLayer.Domain.Entities;
namespace BaseLayer.Application.Services;

public sealed partial class PlatformService(IPlatformRepository repository, ISmartHomeProvider provider, ICredentialProtector protector, TimeProvider clock, HomeOperationGate operations, IUsageLimitReachedService usageLimitReached) : IPlatformService
{
    private const double LimitWatts = SmartUsagePolicy.BatteryLimitWatts;
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
        const double limitWatts = LimitWatts;
        var currentWatts = online ? home.HouseholdWatts : null;
        var reducing = home.SmartPowerOffEnabled && currentWatts >= limitWatts &&
            (home.Commands.Any(c => !CommandPolicy.Terminal(c)) || AutomaticActions(home).Count > 0);
        var candidates = home.SmartPowerOffEnabled
            ? home.Devices.Where(d => d.ShutoffLevel == ShutoffLevels.Sometimes)
            : home.Devices;
        var actions = !reducing && currentWatts >= limitWatts
            ? usageLimitReached.RecommendActions(currentWatts.Value, limitWatts, candidates)
            : [];
        var smartStatus = !home.SmartPowerOffEnabled ? "off"
            : currentWatts is null || !double.IsFinite(currentWatts.Value) ? "unknown"
            : currentWatts < limitWatts ? "monitoring"
            : reducing ? "reducing"
            : currentWatts - actions.Sum(a => a.PowerWatts) >= limitWatts ? "insufficient" : "review";
        var recommendations = actions.Select(action => action.EntityId).ToHashSet();
        var projected = currentWatts is { } watts
            ? (double?)Math.Max(0, watts - actions.Sum(action => action.PowerWatts))
            : null;
        return new(home.Id, home.Name, online, home.Revoked, home.LastSeenUtc, currentWatts, limitWatts, projected,
            home.Devices.Where(d => d.Present).OrderBy(d => d.Name).Select(d => new DeviceDto(d.EntityId, d.Name, online ? d.State : "unavailable", online ? d.PowerWatts : null, d.Allowed, recommendations.Contains(d.EntityId), d.PowerSensorId, d.ThermostatMinF, d.ThermostatMaxF, d.Allowed ? d.ShutoffLevel : ShutoffLevels.Never, SmartPanelCircuit.IsCircuit(d.EntityId), d.EvCurrentEntityId is { } currentId ? new(currentId, d.EvWattsPerAmp) : null, online ? EvControl(home, d) : null, CircuitPriority(home, d, online))).ToList(),
            home.Commands.OrderByDescending(c => c.CreatedUtc).Take(30).Select(Dto).ToList(), home.BaseUrl, home.HouseholdPowerSensorId, home.AllowFutureDevices, Sensors(home), home.PowerSource, home.SmartPowerOffEnabled, smartStatus,
            AutoRestorePolicy.Ordered(home.Devices.Where(d => d.RestoreQueuedUtc != null))
                .Select(d => new RestoreQueueDto(d.EntityId, d.Name, AutoRestorePolicy.Estimate(home, d), d.RestoreQueuedUtc!.Value,
                    d.SmartUsageHeld ? "held" : !home.SmartPowerOffEnabled ? "paused" : AutoRestorePolicy.Estimate(home, d) is null ? "unknown" : d.RestoreStatus, online ? EvControl(home, d)?.Amps : null, d.RestoreCurrentAmps)).ToList(), online ? CurrentControls(home) : []);
    }
    private static CommandDto Dto(DeviceCommand c) => new(c.Id, c.EntityId, c.Status, c.Attempts, c.CreatedUtc, c.Message, c.Automatic, c.Action, c.CurrentAmps, c.CircuitPriority, c.PreviousCurrentAmps, c.IsRestoration);
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
        if (request.ThermostatLimits is { } limits)
            foreach (var (entityId, range) in limits)
                if (!devices.Contains(entityId) || !entityId.StartsWith("climate.") || range is null ||
                    !double.IsFinite(range.MinF) || !double.IsFinite(range.MaxF) ||
                    range.MinF < 10 || range.MaxF > 99 || range.MinF != Math.Truncate(range.MinF) ||
                    range.MaxF != Math.Truncate(range.MaxF) || range.MinF >= range.MaxF)
                    throw new ArgumentException("Choose a thermostat and two-digit whole temperatures with the minimum below the maximum (°F).");
        if (request.ShutoffLevels is { } levels && levels.Any(kv =>
            !devices.Contains(kv.Key) || kv.Value is not (ShutoffLevels.Never or ShutoffLevels.Sometimes or ShutoffLevels.Anytime) ||
            (kv.Key.StartsWith("climate.") && kv.Value == ShutoffLevels.Anytime)))
            throw new ArgumentException("Choose Never, Sometimes, or Anytime for each device. Thermostats cannot be shut off automatically.");
        ValidateEvSettings(home, request);
        if (request.SmartPowerOffEnabled is { } enabled) SetSmartPowerOff(home, enabled);
        AutoRestorePolicy.ResetEligibility(home);
        home.AllowFutureDevices = request.AllowFutureDevices;
        home.PowerSource = request.PowerSource;
        home.HouseholdPowerSensorId = householdMeter;
        home.HouseholdWatts = null;
        foreach (var device in home.Devices)
        {
            ApplyEvSettings(home, device, request);
            device.Allowed = device.Present && (request.AllowAll || request.AllowedEntityIds.Contains(device.EntityId));
            if (request.ShutoffLevels?.TryGetValue(device.EntityId, out var level) == true)
            {
                device.ShutoffLevel = level;
                device.Allowed = device.Present && level != ShutoffLevels.Never;
            }
            else if (!device.Allowed)
                device.ShutoffLevel = ShutoffLevels.Never;
            else if (device.ShutoffLevel == ShutoffLevels.Never)
                device.ShutoffLevel = ShutoffLevels.Sometimes;
            if (!device.Allowed || device.ShutoffLevel == ShutoffLevels.Never) AutoRestorePolicy.Clear(device);
            var nextPowerSensor = request.DevicePowerSensors.GetValueOrDefault(device.EntityId);
            if (device.PowerSensorId != nextPowerSensor)
            {
                device.PowerSensorRevision++;
                device.LastOnWatts = null;
                device.RestoreWatts = null;
            }
            device.PowerSensorId = nextPowerSensor;
            device.PowerWatts = null;
            if (request.ThermostatLimits?.TryGetValue(device.EntityId, out var range) == true)
            {
                device.ThermostatMinF = range.MinF;
                device.ThermostatMaxF = range.MaxF;
            }
        }
        foreach (var c in home.Commands.Where(c => !CommandPolicy.Terminal(c) && !c.ManualCircuit && !home.Devices.Any(d => d.EntityId == c.EntityId && d.Allowed && d.ShutoffLevel != ShutoffLevels.Never && (!c.Automatic || (home.SmartPowerOffEnabled && (c.Action == "On" || d.ShutoffLevel == ShutoffLevels.Anytime))))))
        {
            c.Status = "Cancelled";
            c.Message = "Device access removed.";
        }
        await repository.SaveAsync();
        return ToDto(home);
    }));
    public Task<HomeDto> SmartPowerOffAsync(string owner, Guid id, SmartPowerOffRequest request) => operations.RunAsync(id, () => repository.TransactionAsync(async () =>
    {
        var home = await Owned(owner, id);
        if (home.Revoked)
            throw new ArgumentException("Connection revoked.");
        SetSmartPowerOff(home, request.Enabled);
        await repository.SaveAsync();
        return ToDto(home);
    }));
    private static void SetSmartPowerOff(Home home, bool enabled)
    {
        home.SmartPowerOffEnabled = enabled;
        AutoRestorePolicy.ResetEligibility(home);
        if (enabled) return;
        foreach (var command in home.Commands.Where(c => c.Automatic && !CommandPolicy.Terminal(c)))
        {
            command.Status = "Cancelled";
            command.Message = "Smart Shutoff disabled. Further attempts stopped; requests already sent cannot be undone.";
        }
        foreach (var device in home.Devices.Where(d => d.RestoreStatus == "restoring"))
            device.RestoreStatus = "waiting";
    }
    public Task<HomeDto> KeepOffAsync(string owner, Guid id, string entityId) => operations.RunAsync(id, () => repository.TransactionAsync(async () =>
    {
        var home = await Owned(owner, id);
        var device = home.Devices.SingleOrDefault(d => d.EntityId == entityId) ?? throw new KeyNotFoundException("Device not found.");
        AutoRestorePolicy.Clear(device);
        foreach (var command in home.Commands.Where(c => c.EntityId == entityId && (c.Action == "On" || c.IsRestoration) && !CommandPolicy.Terminal(c)))
        {
            command.Status = "Cancelled";
            command.Message = "Removed from the restore queue. Requests already sent cannot be undone.";
        }
        await repository.SaveAsync();
        return ToDto(home);
    }));
    private IReadOnlyList<TurnOffRecommendation> AutomaticActions(Home home)
    {
        if (!home.SmartPowerOffEnabled || home.HouseholdWatts is not { } watts || !double.IsFinite(watts))
            return [];
        var eventKey = $"smart:{home.SmartPowerOffEventId}";
        var attempted = home.Commands.Where(c => c.Automatic && c.Action == "Off" && c.IdempotencyKey == eventKey).Select(c => c.EntityId).ToHashSet();
        return usageLimitReached.RecommendActions(watts, LimitWatts,
            home.Devices.Where(d => d.ShutoffLevel == ShutoffLevels.Anytime && !attempted.Contains(d.EntityId)));
    }
    private void QueueSmartPowerOff(Home home)
    {
        // Only a successful poll reaches here. Wait for all outstanding shutoffs
        // before planning another batch from measured usage, never projected usage.
        if (!home.SmartPowerOffEnabled || home.HouseholdWatts is not { } watts || !double.IsFinite(watts) || watts < LimitWatts
            || home.Commands.Any(c => !CommandPolicy.Terminal(c)))
            return;
        home.SmartPowerOffEventId ??= Guid.NewGuid().ToString("N");
        if (QueueEvReduction(home)) return;
        foreach (var action in AutomaticActions(home))
            home.Commands.Add(new DeviceCommand
            {
                HomeId = home.Id, EntityId = action.EntityId, RequestedBy = home.OwnerId,
                IdempotencyKey = $"smart:{home.SmartPowerOffEventId}", Automatic = true,
                CreatedUtc = Now, ExpiresUtc = Now.AddMinutes(2), NextAttemptUtc = Now,
                Message = "Smart Power Off selected this Anytime device to reduce usage."
            });
    }
    public Task<CommandDto> CircuitCommandAsync(string owner, Guid id, CircuitCommandRequest request) => operations.RunAsync(id, () => repository.TransactionAsync(async () =>
    {
        var home = await Owned(owner, id);
        if (string.IsNullOrWhiteSpace(request.EntityId) || !SmartPanelCircuit.IsCircuit(request.EntityId) ||
            request.Action is not ("On" or "Off") || string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 100)
            throw new ArgumentException("Choose a panel circuit and an On or Off action.");
        var prior = home.Commands.Where(c => c.IdempotencyKey == request.IdempotencyKey).ToList();
        if (prior.Count > 0)
        {
            if (prior.Count != 1 || !prior[0].ManualCircuit || prior[0].EntityId != request.EntityId || prior[0].Action != request.Action)
                throw new ArgumentException("Idempotency key was used for a different command.");
            return Dto(prior[0]);
        }
        if (!Fresh(home, Now) || home.ProtectedTokens is null)
            throw new ArgumentException("Home is offline. Reconnect before controlling circuits.");
        var device = home.Devices.SingleOrDefault(d => d.EntityId == request.EntityId && d.Present);
        if (device is null || device.State is not ("on" or "off"))
            throw new ArgumentException("The circuit is unavailable. Wait for a fresh Home Assistant reading.");
        foreach (var existing in home.Commands) CommandPolicy.Advance(existing, Now);
        if (home.Commands.Any(c => c.EntityId == request.EntityId && !CommandPolicy.Terminal(c)))
            throw new ArgumentException("A command is already pending for this circuit.");
        // Manual panel control is independent of appliance automation permissions.
        AutoRestorePolicy.Clear(device);
        var command = new DeviceCommand
        {
            HomeId = id, EntityId = request.EntityId, Action = request.Action, ManualCircuit = true,
            RequestedBy = owner, IdempotencyKey = request.IdempotencyKey, CreatedUtc = Now,
            ExpiresUtc = Now.AddMinutes(2), NextAttemptUtc = Now,
            Message = "Waiting for Home Assistant to confirm the circuit state."
        };
        home.Commands.Add(command);
        await repository.SaveAsync();
        return Dto(command);
    }));
    public Task<List<CommandDto>> TurnOffAsync(string owner, Guid id, TurnOffRequest request) => operations.RunAsync(id, () => repository.TransactionAsync(async () =>
    {
        var home = await Owned(owner, id);
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 100 || request.EntityIds.Count is < 1 or > 100)
            throw new ArgumentException("Invalid command request.");
        if (request.EntityIds.Any(id => id.StartsWith("climate.")))
            throw new ArgumentException("Thermostats require temperature adjustments, not shutoff commands.");
        var prior = home.Commands.Where(c => c.IdempotencyKey == request.IdempotencyKey).ToList();
        if (prior.Count > 0)
        {
            if (prior.Any(c => c.ManualCircuit || c.Action != "Off") || !prior.Select(c => c.EntityId).ToHashSet().SetEquals(request.EntityIds))
                throw new ArgumentException("Idempotency key was used for different devices.");
            return prior.Select(Dto).ToList();
        }
        if (!Fresh(home, Now))
            throw new ArgumentException("Home is offline. Reconnect before controlling devices.");
        var ids = request.EntityIds.Distinct().ToList();
        foreach (var entityId in ids)
            if (!home.Devices.Any(d => d.EntityId == entityId && d.Allowed && d.ShutoffLevel != ShutoffLevels.Never && Active(d)))
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
            if ((command.Action == "On" || command.IsRestoration) && home.Devices.SingleOrDefault(d => d.EntityId == command.EntityId) is { } device)
                AutoRestorePolicy.Clear(device);
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
        home.CurrentControlsJson = JsonSerializer.Serialize(snapshot.CurrentControls ?? []);
        home.CircuitPrioritiesJson = JsonSerializer.Serialize(snapshot.CircuitPriorities ?? []);
        foreach (var device in home.Devices)
            device.Present = false;
        foreach (var reading in snapshot.Devices)
        {
            var device = home.Devices.SingleOrDefault(d => d.EntityId == reading.EntityId);
            if (device is null)
            {
                device = new Device { HomeId = home.Id, EntityId = reading.EntityId, Allowed = home.AllowFutureDevices, ShutoffLevel = home.AllowFutureDevices ? ShutoffLevels.Sometimes : ShutoffLevels.Never };
                home.Devices.Add(device);
            }
            if (device.State == "off" && reading.State == "on") device.SmartUsageHeld = false;
            device.Name = reading.Name;
            device.State = reading.State;
            device.Present = true;
            device.PowerWatts = device.PowerSensorId is { } sensor ? snapshot.Power.GetValueOrDefault(sensor) : null;
            if (device.State == "on" && SmartUsagePolicy.Positive(device.PowerWatts))
                device.LastOnWatts = device.PowerWatts;
            UpdateEvRestoreEstimate(home, device);
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
        ProviderSnapshot snapshot;
        try
        {
            if (tokens.ExpiresUtc <= Now.AddMinutes(1))
                await RefreshTokensAsync();
            try
            {
                snapshot = await provider.ReadAsync(home.BaseUrl, tokens);
            }
            catch (HttpRequestException exception) when (exception.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                // A token may be rejected before its recorded expiry. Recover once;
                // a rejected refresh or second read must reach the failure handler.
                await RefreshTokensAsync();
                snapshot = await provider.ReadAsync(home.BaseUrl, tokens);
            }
        }
        catch { AutoRestorePolicy.ResetEligibility(home); foreach (var c in home.Commands) CommandPolicy.Advance(c, Now); await repository.SaveAsync(); throw; }
        async Task RefreshTokensAsync()
        {
            tokens = await provider.RefreshAsync(home.BaseUrl, tokens);
            home.ProtectedTokens = protector.Protect(tokens);
            await repository.SaveAsync();
        }
        if (!Fresh(home, Now)) AutoRestorePolicy.ResetEligibility(home);
        ApplySnapshot(home, snapshot);
        AutoRestorePolicy.QueueNext(home, Now, LimitWatts);
        QueueSmartPowerOff(home);
        var turnOnSentThisPoll = false;
        foreach (var command in home.Commands.Where(c => !CommandPolicy.Terminal(c)))
        {
            // Expiry is checked before observations: stale approvals never trigger another attempt.
            if (command.ExpiresUtc <= Now)
                CommandPolicy.Advance(command, Now);
            if (CommandPolicy.Terminal(command))
                continue;
            var device = home.Devices.SingleOrDefault(d => d.EntityId == command.EntityId && d.Present);
            if (device is null || (command.ManualCircuit
                ? !SmartPanelCircuit.IsCircuit(device.EntityId)
                : !device.Allowed || device.ShutoffLevel == ShutoffLevels.Never || device.EntityId.StartsWith("climate.") || SmartPanelCircuit.IsCircuit(device.EntityId))
                || (command.Automatic && (!home.SmartPowerOffEnabled || (command.Action != "On" && device.ShutoffLevel != ShutoffLevels.Anytime))))
            {
                command.Status = "Cancelled";
                command.Message = "Device unavailable or access removed.";
                continue;
            }
            if (command.Action == "SetCircuitPriority")
            {
                await ProcessCircuitPriority(home, device, command, tokens);
                continue;
            }
            if (command.Action == "SetCurrent")
            {
                await ProcessEvCommand(home, device, command, tokens);
                continue;
            }
            var reachedState = command.Action == "On" ? Active(device) : device.State == "off";
            if (command.Attempts > 0 && reachedState)
            {
                if (command.ManualCircuit)
                {
                    command.Status = "Confirmed";
                    command.Message = $"Home Assistant confirmed circuit {command.Action.ToLowerInvariant()}.";
                }
                else AutoRestorePolicy.Confirm(home, device, command, Now);
                continue;
            }
            if (!command.ManualCircuit && command.Action == "On" && !(command.UsageRevision is not null
                ? SmartUsagePolicy.CanTurnOn(home, device, command)
                : AutoRestorePolicy.Fits(home, device, LimitWatts)))
            {
                command.Status = "Cancelled";
                command.Message = "Restoration paused: spare capacity or device state changed.";
                device.RestoreStatus = "waiting";
                device.RestoreEligibleSinceUtc = null;
                continue;
            }
            if (command.Automatic && command.Action == "Off" && (home.HouseholdWatts is not { } current || !double.IsFinite(current) || current < LimitWatts))
            {
                command.Status = "Cancelled";
                command.Message = "Automatic shutoff stopped: usage is below the limit or unavailable.";
                continue;
            }
            CommandPolicy.Advance(command, Now);
            if (CommandPolicy.Terminal(command) || command.Status == "AwaitingConfirmation" || command.NextAttemptUtc > Now)
                continue;
            if (reachedState)
            {
                command.Status = "Confirmed";
                command.Message = "Device is already in the requested state.";
                continue;
            }
            if ((command.ManualCircuit && device.State is not ("on" or "off")) || (command.Action == "Off" && !Active(device)))
            {
                CommandPolicy.Retry(command, Now, "Device state is unavailable.");
                continue;
            }
            if (command.Action == "Off" && command.Attempts == 0)
            {
                command.EstimatedWatts = device.PowerWatts is { } power && double.IsFinite(power) && power > 0 ? power : null;
                if (device.EvCurrentEntityId is not null)
                    command.EstimatedWatts = EvRatedWatts(home, device) is { } rated ? Math.Max(command.EstimatedWatts ?? 0, rated) : null;
            }
            // Turn-ons are serialized across fresh polls; never spend the same headroom twice.
            if (command.Action == "On" && (turnOnSentThisPoll || home.Commands.Any(c =>
                c.Id != command.Id && c.Action == "On" && c.Attempts > 0 && !CommandPolicy.Terminal(c))))
                continue;
            command.Attempts++;
            command.Status = "AwaitingConfirmation";
            command.LeaseUntilUtc = Now.AddSeconds(10);
            await repository.SaveAsync();
            try
            {
                if (command.Action == "On")
                {
                    turnOnSentThisPoll = true;
                    home.LastRestoreUtc = Now;
                    await repository.SaveAsync();
                    await provider.TurnOnAsync(home.BaseUrl, tokens, command.EntityId);
                }
                else
                    await provider.TurnOffAsync(home.BaseUrl, tokens, command.EntityId);
            }
            catch (HttpRequestException) { CommandPolicy.Retry(command, Now, "Home Assistant rejected or could not receive the command."); }
            catch (TaskCanceledException) { CommandPolicy.Retry(command, Now, "Home Assistant command timed out."); }
        }
        if (home.HouseholdWatts is { } measured && double.IsFinite(measured) && measured < LimitWatts)
            home.SmartPowerOffEventId = null;
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

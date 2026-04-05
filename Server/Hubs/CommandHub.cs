using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using ClassroomController.Server.Data;
using ClassroomController.Server.Middleware;
using ClassroomController.Server.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace ClassroomController.Server.Hubs;

public class CommandHub : Hub
{
    private readonly AppDbContext _dbContext;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CommandHub> _logger;

    private static readonly ConcurrentDictionary<string, string> ConnectedClients = new();

    public CommandHub(AppDbContext dbContext, IConfiguration configuration, ILogger<CommandHub> logger)
    {
        _dbContext = dbContext;
        _configuration = configuration;
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        var connectionId = Context.ConnectionId;
        var clientIp = Context.GetHttpContext()?.Connection.RemoteIpAddress?.ToString();
        var providedKey = Context.GetHttpContext()?.Request.Query["key"].ToString();
        var requestedMac = NormalizeMac(Context.GetHttpContext()?.Request.Query["mac"].ToString());
        var expectedKey = _configuration["MasterKey"];

        if (string.IsNullOrWhiteSpace(expectedKey) || !string.Equals(providedKey, expectedKey, StringComparison.Ordinal))
        {
            _logger.LogWarning("[Hub] Rejected unauthorized connection {ConnectionId} from {ClientIp}", connectionId, clientIp);
            Context.Abort();
            return;
        }

        _logger.LogInformation("[Hub] Client connected {ConnectionId} from {ClientIp}", connectionId, clientIp);

        var devices = await _dbContext.Devices.ToListAsync();
        if (NormalizeExpiredTimers(devices))
        {
            await _dbContext.SaveChangesAsync();
        }

        _logger.LogInformation("[Hub] Sending {DeviceCount} devices to {ConnectionId}", devices.Count, connectionId);
        await Clients.Caller.SendAsync("UpdateDeviceList", devices);

        if (!string.IsNullOrWhiteSpace(requestedMac))
        {
            var device = devices.FirstOrDefault(d => NormalizeMac(d.MacAddress) == requestedMac);
            if (device != null)
            {
                await SendSyncStateAsync(connectionId, device);
            }
        }

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var connectionId = Context.ConnectionId;
        _logger.LogInformation("[Hub] Client disconnected {ConnectionId} (reason: {Reason})", connectionId, exception?.Message ?? "none");

        var macToRemove = ConnectedClients.FirstOrDefault(kvp => kvp.Value == connectionId).Key;
        if (!string.IsNullOrWhiteSpace(macToRemove))
        {
            ConnectedClients.TryRemove(macToRemove, out _);
            _logger.LogInformation("[Hub] Removed {MacAddress} from connected clients", macToRemove);

            var device = await _dbContext.Devices.FirstOrDefaultAsync(d => d.MacAddress.ToUpper() == macToRemove.ToUpper());
            if (device != null)
            {
                var shouldRemainOnline = StreamRelayMiddleware.IsStudentStreamConnected(macToRemove);
                device.Status = shouldRemainOnline ? "Online" : "Offline";
                await _dbContext.SaveChangesAsync();
                await Clients.All.SendAsync("DeviceStatusChanged", macToRemove, device.Status);
                await BroadcastDeviceStateAsync(device);
            }
        }

        await base.OnDisconnectedAsync(exception);
    }

    public async Task UpdateStatus(string macAddress, string status)
    {
        if (string.IsNullOrWhiteSpace(macAddress) || string.IsNullOrWhiteSpace(status))
        {
            _logger.LogWarning("[Hub] UpdateStatus rejected for {ConnectionId}: empty MAC or status", Context.ConnectionId);
            return;
        }

        var normalizedMac = NormalizeMac(macAddress);
        var effectiveStatus = status;
        if (!status.Equals("Online", StringComparison.OrdinalIgnoreCase)
            && StreamRelayMiddleware.IsStudentStreamConnected(normalizedMac))
        {
            effectiveStatus = "Online";
        }

        var device = await _dbContext.Devices.FirstOrDefaultAsync(d => d.MacAddress.ToUpper() == normalizedMac);
        if (device == null)
        {
            _logger.LogWarning("[Hub] UpdateStatus rejected for unknown device {MacAddress}", normalizedMac);
            return;
        }

        var stateChanged = PromoteExpiredTimerToLock(device);

        if (effectiveStatus.Equals("Online", StringComparison.OrdinalIgnoreCase))
        {
            ConnectedClients.AddOrUpdate(normalizedMac, Context.ConnectionId, (_, _) => Context.ConnectionId);
            _logger.LogInformation("[Hub] Device {Hostname} ({MacAddress}) is Online. Active clients: {ConnectedCount}", device.Hostname, normalizedMac, ConnectedClients.Count);
        }
        else
        {
            if (ConnectedClients.TryRemove(normalizedMac, out _))
            {
                _logger.LogInformation("[Hub] Device {Hostname} ({MacAddress}) reported Offline. Active clients: {ConnectedCount}", device.Hostname, normalizedMac, ConnectedClients.Count);
            }
        }

        device.Status = effectiveStatus;
        await _dbContext.SaveChangesAsync();
        await Clients.All.SendAsync("DeviceStatusChanged", normalizedMac, effectiveStatus);
        _logger.LogInformation("[Hub] Broadcast status change {MacAddress} -> {Status}", normalizedMac, effectiveStatus);

        if (stateChanged)
        {
            await BroadcastDeviceStateAsync(device);
        }

        if (effectiveStatus.Equals("Online", StringComparison.OrdinalIgnoreCase))
        {
            await SendSyncStateAsync(Context.ConnectionId, device);
        }
    }

    public async Task<bool> SendCommand(string targetMac, string action, object payload)
    {
        var normalizedTargetMac = NormalizeMac(targetMac);
        _logger.LogInformation("[Hub] SendCommand {Action} -> {MacAddress}", action, normalizedTargetMac);

        if (!ConnectedClients.TryGetValue(normalizedTargetMac, out var connectionId))
        {
            _logger.LogWarning("[Hub] Command {Action} not sent because {MacAddress} is offline", action, normalizedTargetMac);
            return false;
        }

        await Clients.Client(connectionId).SendAsync("ExecuteCommand", action, payload);
        _logger.LogInformation("[Hub] Command {Action} sent to {MacAddress}", action, targetMac);
        return true;
    }

    public async Task SendMouseInput(string targetMac, string eventType, double xPct, double yPct, int button)
    {
        var normalizedTargetMac = NormalizeMac(targetMac);

        if (!ConnectedClients.TryGetValue(normalizedTargetMac, out var connectionId))
        {
            _logger.LogWarning("[Hub] Mouse input {EventType} not sent because {MacAddress} is offline", eventType, normalizedTargetMac);
            return;
        }

        await Clients.Client(connectionId).SendAsync("ReceiveMouseInput", eventType, xPct, yPct, button);
    }

    public async Task SendKeyboardInput(string targetMac, string eventType, int keyCode)
    {
        var normalizedTargetMac = NormalizeMac(targetMac);

        if (!ConnectedClients.TryGetValue(normalizedTargetMac, out var connectionId))
        {
            _logger.LogWarning("[Hub] Keyboard input {EventType} not sent because {MacAddress} is offline", eventType, normalizedTargetMac);
            return;
        }

        await Clients.Client(connectionId).SendAsync("ReceiveKeyboardInput", eventType, keyCode);
    }

    public async Task SetStreamQuality(string targetMac, string quality)
    {
        var normalizedTargetMac = NormalizeMac(targetMac);

        if (!ConnectedClients.TryGetValue(normalizedTargetMac, out var connectionId))
        {
            _logger.LogWarning("[Hub] Stream quality {Quality} not sent because {MacAddress} is offline", quality, normalizedTargetMac);
            return;
        }

        await Clients.Client(connectionId).SendAsync("SetStreamQuality", quality);
    }

    public async Task ToggleLock(string targetMac)
    {
        var normalizedTargetMac = NormalizeMac(targetMac);
        var device = await GetDeviceAsync(normalizedTargetMac);
        if (device == null)
        {
            return;
        }

        device.IsLocked = !device.IsLocked;
        if (device.IsLocked)
        {
            device.IsFrozen = false;
            device.TimerEndTime = null;
        }

        await _dbContext.SaveChangesAsync();

        if (ConnectedClients.TryGetValue(normalizedTargetMac, out var connectionId))
        {
            if (device.IsLocked)
            {
                await Clients.Client(connectionId).SendAsync("ReceiveFreezeUpdate", false);
                await Clients.Client(connectionId).SendAsync("ReceiveStopTimer");
                await Clients.Client(connectionId).SendAsync("ReceiveLock");
            }
            else
            {
                await Clients.Client(connectionId).SendAsync("ReceiveUnlock");
            }
        }

        await BroadcastDeviceStateAsync(device);
    }

    public async Task ToggleFreeze(string targetMac)
    {
        var normalizedTargetMac = NormalizeMac(targetMac);
        var device = await GetDeviceAsync(normalizedTargetMac);
        if (device == null)
        {
            return;
        }

        device.IsFrozen = !device.IsFrozen;
        if (device.IsFrozen)
        {
            device.IsLocked = false;
            device.TimerEndTime = null;
        }

        await _dbContext.SaveChangesAsync();

        if (ConnectedClients.TryGetValue(normalizedTargetMac, out var connectionId))
        {
            if (device.IsFrozen)
            {
                await Clients.Client(connectionId).SendAsync("ReceiveUnlock");
                await Clients.Client(connectionId).SendAsync("ReceiveStopTimer");
            }

            await Clients.Client(connectionId).SendAsync("ReceiveFreezeUpdate", device.IsFrozen);
        }

        await BroadcastDeviceStateAsync(device);
    }

    public async Task SetTimer(string targetMac, double minutes)
    {
        var normalizedTargetMac = NormalizeMac(targetMac);
        var device = await GetDeviceAsync(normalizedTargetMac);
        if (device == null)
        {
            return;
        }

        if (minutes <= 0)
        {
            _logger.LogWarning("[Hub] SetTimer rejected for {MacAddress}: non-positive duration {Minutes}", normalizedTargetMac, minutes);
            return;
        }

        device.TimerEndTime = DateTime.UtcNow.AddMinutes(minutes);
        device.IsLocked = false;
        device.IsFrozen = false;
        await _dbContext.SaveChangesAsync();

        if (ConnectedClients.TryGetValue(normalizedTargetMac, out var connectionId))
        {
            await Clients.Client(connectionId).SendAsync("ReceiveUnlock");
            await Clients.Client(connectionId).SendAsync("ReceiveFreezeUpdate", false);
            await Clients.Client(connectionId).SendAsync("ReceiveStartTimer", device.TimerEndTime?.ToString("O"));
        }

        await BroadcastDeviceStateAsync(device);
    }

    public async Task PowerOnDevice(string targetMac)
    {
        var normalizedTargetMac = NormalizeMac(targetMac);
        var device = await GetDeviceAsync(normalizedTargetMac);
        if (device == null)
        {
            return;
        }

        try
        {
            await SendWakeOnLanAsync(device.MacAddress, device.IpAddress);
            _logger.LogInformation("[Hub] Wake-on-LAN packet sent to {Hostname} ({MacAddress})", device.Hostname, device.MacAddress);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Hub] Wake-on-LAN failed for {Hostname} ({MacAddress})", device.Hostname, device.MacAddress);
            throw;
        }
    }

    public async Task ToggleAdminMode(string targetMac)
    {
        var normalizedTargetMac = NormalizeMac(targetMac);
        var device = await GetDeviceAsync(normalizedTargetMac);
        if (device == null)
        {
            return;
        }

        device.IsAdminMode = !device.IsAdminMode;
        await _dbContext.SaveChangesAsync();

        if (ConnectedClients.TryGetValue(normalizedTargetMac, out var connectionId))
        {
            await Clients.Client(connectionId).SendAsync("ReceiveAdminModeUpdate", device.IsAdminMode);
        }

        await BroadcastDeviceStateAsync(device);
    }

    public async Task SetAdminMode(string targetMac, bool enableAdmin)
    {
        var normalizedTargetMac = NormalizeMac(targetMac);
        var device = await GetDeviceAsync(normalizedTargetMac);
        if (device == null)
        {
            return;
        }

        device.IsAdminMode = enableAdmin;
        await _dbContext.SaveChangesAsync();

        if (ConnectedClients.TryGetValue(normalizedTargetMac, out var connectionId))
        {
            await Clients.Client(connectionId).SendAsync("ReceiveAdminModeUpdate", device.IsAdminMode);
        }

        await BroadcastDeviceStateAsync(device);
    }

    public async Task ToggleWebsiteBlock(string targetMac, string domain, bool block)
    {
        var normalizedTargetMac = NormalizeMac(targetMac);
        var normalizedDomain = NormalizeDomain(domain);
        var device = await GetDeviceAsync(normalizedTargetMac);
        if (device == null)
        {
            return;
        }

        var blockedDomains = ParseBlockedWebsites(device.BlockedWebsites);
        if (block)
        {
            blockedDomains.Add(normalizedDomain);
        }
        else
        {
            blockedDomains.Remove(normalizedDomain);
        }

        device.BlockedWebsites = string.Join(",", blockedDomains.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
        await _dbContext.SaveChangesAsync();

        if (ConnectedClients.TryGetValue(normalizedTargetMac, out var connectionId))
        {
            await Clients.Client(connectionId).SendAsync("ReceiveWebsiteBlock", normalizedDomain, block);
        }
        else
        {
            _logger.LogWarning("[Hub] Website block {Domain}={Block} persisted while {MacAddress} is offline", normalizedDomain, block, normalizedTargetMac);
        }

        await BroadcastDeviceStateAsync(device);
    }

    public async Task StartSoftLock(string targetMac)
    {
        var normalizedTargetMac = NormalizeMac(targetMac);
        var device = await GetDeviceAsync(normalizedTargetMac);
        if (device == null)
        {
            return;
        }

        device.IsFrozen = true;
        device.IsLocked = false;
        device.TimerEndTime = null;
        await _dbContext.SaveChangesAsync();

        if (ConnectedClients.TryGetValue(normalizedTargetMac, out var connectionId))
        {
            await Clients.Client(connectionId).SendAsync("ReceiveUnlock");
            await Clients.Client(connectionId).SendAsync("ReceiveStopTimer");
            await Clients.Client(connectionId).SendAsync("ReceiveFreezeUpdate", true);
        }
        else
        {
            _logger.LogWarning("[Hub] StartSoftLock persisted while {MacAddress} is offline", normalizedTargetMac);
        }

        await BroadcastDeviceStateAsync(device);
    }

    public async Task StopSoftLock(string targetMac)
    {
        var normalizedTargetMac = NormalizeMac(targetMac);
        var device = await GetDeviceAsync(normalizedTargetMac);
        if (device == null)
        {
            return;
        }

        device.IsFrozen = false;
        await _dbContext.SaveChangesAsync();

        if (ConnectedClients.TryGetValue(normalizedTargetMac, out var connectionId))
        {
            await Clients.Client(connectionId).SendAsync("ReceiveFreezeUpdate", false);
        }
        else
        {
            _logger.LogWarning("[Hub] StopSoftLock persisted while {MacAddress} is offline", normalizedTargetMac);
        }

        await BroadcastDeviceStateAsync(device);
    }

    public async Task StartTimer(string targetMac, double minutes)
    {
        await SetTimer(targetMac, minutes);
    }

    public async Task StopTimer(string targetMac)
    {
        var normalizedTargetMac = NormalizeMac(targetMac);
        var device = await GetDeviceAsync(normalizedTargetMac);
        if (device == null)
        {
            return;
        }

        device.TimerEndTime = null;
        await _dbContext.SaveChangesAsync();

        if (ConnectedClients.TryGetValue(normalizedTargetMac, out var connectionId))
        {
            await Clients.Client(connectionId).SendAsync("ReceiveStopTimer");
        }

        await BroadcastDeviceStateAsync(device);
    }

    private async Task<Device?> GetDeviceAsync(string normalizedTargetMac)
    {
        var device = await _dbContext.Devices.FirstOrDefaultAsync(d => d.MacAddress.ToUpper() == normalizedTargetMac);
        if (device == null)
        {
            _logger.LogWarning("[Hub] Unknown device {MacAddress}", normalizedTargetMac);
            return null;
        }

        if (PromoteExpiredTimerToLock(device))
        {
            await _dbContext.SaveChangesAsync();
        }

        return device;
    }

    private async Task SendSyncStateAsync(string connectionId, Device device)
    {
        await Clients.Client(connectionId).SendAsync(
            "SyncState",
            device.IsLocked,
            device.IsFrozen,
            device.IsAdminMode,
            device.TimerEndTime,
            device.BlockedWebsites);
    }

    private async Task BroadcastDeviceStateAsync(Device device)
    {
        await Clients.All.SendAsync("DeviceStateChanged", device);
    }

    private static string NormalizeMac(string? macAddress) => macAddress?.Trim().ToUpperInvariant() ?? string.Empty;

    public static bool IsDeviceConnected(string? macAddress)
    {
        var normalizedMac = NormalizeMac(macAddress);
        return !string.IsNullOrWhiteSpace(normalizedMac) && ConnectedClients.ContainsKey(normalizedMac);
    }

    private static string NormalizeDomain(string domain) => domain.Trim().ToLowerInvariant();

    private static HashSet<string> ParseBlockedWebsites(string? blockedWebsites)
    {
        return blockedWebsites?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeDomain)
            .Where(domain => !string.IsNullOrWhiteSpace(domain))
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private static bool PromoteExpiredTimerToLock(Device device)
    {
        if (!device.TimerEndTime.HasValue || device.TimerEndTime.Value > DateTime.UtcNow)
        {
            return false;
        }

        device.IsLocked = true;
        device.TimerEndTime = null;
        return true;
    }

    private static bool NormalizeExpiredTimers(IEnumerable<Device> devices)
    {
        var hasChanges = false;

        foreach (var device in devices)
        {
            hasChanges |= PromoteExpiredTimerToLock(device);
        }

        return hasChanges;
    }

    private static async Task SendWakeOnLanAsync(string macAddress, string ipAddress)
    {
        var macBytes = ParseMacAddress(macAddress);
        var packet = new byte[6 + (16 * macBytes.Length)];

        for (var i = 0; i < 6; i++)
        {
            packet[i] = 0xFF;
        }

        for (var i = 6; i < packet.Length; i += macBytes.Length)
        {
            Buffer.BlockCopy(macBytes, 0, packet, i, macBytes.Length);
        }

        using var udpClient = new UdpClient();
        udpClient.EnableBroadcast = true;

        var endpoints = BuildWakeEndpoints(ipAddress)
            .SelectMany(address => new[]
            {
                new IPEndPoint(address, 9),
                new IPEndPoint(address, 7)
            })
            .DistinctBy(endpoint => endpoint.ToString())
            .ToArray();

        foreach (var endpoint in endpoints)
        {
            await udpClient.SendAsync(packet, packet.Length, endpoint);
        }
    }

    private static byte[] ParseMacAddress(string macAddress)
    {
        var normalizedMac = macAddress
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(":", string.Empty, StringComparison.Ordinal)
            .Trim();

        if (normalizedMac.Length != 12)
        {
            throw new InvalidOperationException($"Invalid MAC address '{macAddress}'.");
        }

        return Enumerable.Range(0, 6)
            .Select(index => Convert.ToByte(normalizedMac.Substring(index * 2, 2), 16))
            .ToArray();
    }

    private static IEnumerable<IPAddress> BuildWakeEndpoints(string ipAddress)
    {
        yield return IPAddress.Broadcast;

        if (!IPAddress.TryParse(ipAddress, out var parsedAddress) || parsedAddress.AddressFamily != AddressFamily.InterNetwork)
        {
            yield break;
        }

        var addressBytes = parsedAddress.GetAddressBytes();
        addressBytes[3] = 255;
        yield return new IPAddress(addressBytes);
    }
}

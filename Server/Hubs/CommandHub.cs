using System.Collections.Concurrent;
using ClassroomController.Server.Data;
using ClassroomController.Server.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace ClassroomController.Server.Hubs;

public class CommandHub : Hub
{
    private readonly AppDbContext _dbContext;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CommandHub> _logger;
    
    // Thread-safe dictionary mapping MAC addresses to SignalR ConnectionIds
    private static ConcurrentDictionary<string, string> _connectedClients = new ConcurrentDictionary<string, string>();

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
        var expectedKey = _configuration["MasterKey"];

        if (string.IsNullOrWhiteSpace(expectedKey) || !string.Equals(providedKey, expectedKey, StringComparison.Ordinal))
        {
            _logger.LogWarning("[Hub] Rejected unauthorized connection {ConnectionId} from {ClientIp}", connectionId, clientIp);
            Context.Abort();
            return;
        }

        _logger.LogInformation("[Hub] Client connected {ConnectionId} from {ClientIp}", connectionId, clientIp);

        // Send the full device list to the caller immediately.
        var devices = await _dbContext.Devices.AsNoTracking().ToListAsync();
        _logger.LogInformation("[Hub] Sending {DeviceCount} devices to {ConnectionId}", devices.Count, connectionId);
        await Clients.Caller.SendAsync("UpdateDeviceList", devices);

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var connectionId = Context.ConnectionId;
        _logger.LogInformation("[Hub] Client disconnected {ConnectionId} (reason: {Reason})", connectionId, exception?.Message ?? "none");

        // Find and remove the MAC address associated with this connection ID
        var macToRemove = _connectedClients
            .FirstOrDefault(kvp => kvp.Value == connectionId).Key;
        
        if (macToRemove != null)
        {
            _connectedClients.TryRemove(macToRemove, out _);
            _logger.LogInformation("[Hub] Removed {MacAddress} from connected clients", macToRemove);

            // Update device status to Offline
            var device = await _dbContext.Devices
                .FirstOrDefaultAsync(d => d.MacAddress.ToUpper() == macToRemove.ToUpper());
            if (device != null)
            {
                device.Status = "Offline";
                await _dbContext.SaveChangesAsync();
                _logger.LogInformation("[Hub] Device {Hostname} ({MacAddress}) marked Offline after disconnect", device.Hostname, macToRemove);
                await Clients.All.SendAsync("DeviceStatusChanged", macToRemove, "Offline");
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

        var normalizedMac = macAddress.Trim().ToUpperInvariant();

        var device = await _dbContext.Devices
            .FirstOrDefaultAsync(d => d.MacAddress.ToUpper() == normalizedMac);
        if (device == null)
        {
            _logger.LogWarning("[Hub] UpdateStatus rejected for unknown device {MacAddress}", normalizedMac);
            return;
        }

        // Manage connected client map depending on status
        if (status.Equals("Online", StringComparison.OrdinalIgnoreCase))
        {
            _connectedClients.AddOrUpdate(normalizedMac, Context.ConnectionId, (key, oldValue) => Context.ConnectionId);
            _logger.LogInformation("[Hub] Device {Hostname} ({MacAddress}) is Online. Active clients: {ConnectedCount}", device.Hostname, normalizedMac, _connectedClients.Count);
        }
        else
        {
            // If reporting offline, remove mapping
            if (_connectedClients.TryRemove(normalizedMac, out _))
            {
                _logger.LogInformation("[Hub] Device {Hostname} ({MacAddress}) reported Offline. Active clients: {ConnectedCount}", device.Hostname, normalizedMac, _connectedClients.Count);
            }
        }

        device.Status = status;
        await _dbContext.SaveChangesAsync();
        await Clients.All.SendAsync("DeviceStatusChanged", normalizedMac, status);
        _logger.LogInformation("[Hub] Broadcast status change {MacAddress} -> {Status}", normalizedMac, status);
    }

    public async Task<bool> SendCommand(string targetMac, string action, object payload)
    {
        var normalizedTargetMac = targetMac.Trim().ToUpperInvariant();
        _logger.LogInformation("[Hub] SendCommand {Action} -> {MacAddress}", action, normalizedTargetMac);

        // Look up the connection ID for the target MAC
        if (!_connectedClients.TryGetValue(normalizedTargetMac, out var connectionId))
        {
            _logger.LogWarning("[Hub] Command {Action} not sent because {MacAddress} is offline", action, normalizedTargetMac);
            return false;
        }

        var commandEnvelope = new
        {
            targetMac,
            action,
            payload,
            timestamp = DateTime.UtcNow
        };

        // Send command strictly to the target client
        await Clients.Client(connectionId).SendAsync("ExecuteCommand", action, payload);
        _logger.LogInformation("[Hub] Command {Action} sent to {MacAddress}", action, targetMac);
        return true;
    }

    public async Task SendMouseInput(string targetMac, string eventType, double xPct, double yPct)
    {
        var normalizedTargetMac = targetMac.Trim().ToUpperInvariant();

        if (!_connectedClients.TryGetValue(normalizedTargetMac, out var connectionId))
        {
            _logger.LogWarning("[Hub] Mouse input {EventType} not sent because {MacAddress} is offline", eventType, normalizedTargetMac);
            return;
        }

        await Clients.Client(connectionId).SendAsync("ReceiveMouseInput", eventType, xPct, yPct);
    }
}

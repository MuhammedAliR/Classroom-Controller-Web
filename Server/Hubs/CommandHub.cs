using ClassroomController.Server.Data;
using ClassroomController.Server.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace ClassroomController.Server.Hubs;

public class CommandHub : Hub
{
    private readonly AppDbContext _dbContext;

    public CommandHub(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public override async Task OnConnectedAsync()
    {
        var connectionId = Context.ConnectionId;
        var clientIp = Context.GetHttpContext()?.Connection.RemoteIpAddress?.ToString();

        Console.WriteLine($"CommandHub: client connected: {connectionId} from {clientIp}");

        // Send the full device list to the caller immediately.
        var devices = await _dbContext.Devices.AsNoTracking().ToListAsync();
        Console.WriteLine($"CommandHub: Sending {devices.Count} devices to {connectionId}");
        foreach (var d in devices)
        {
            Console.WriteLine($"  -> MAC: {d.MacAddress}, Hostname: {d.Hostname}, Status: {d.Status}");
        }
        await Clients.Caller.SendAsync("UpdateDeviceList", devices);

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var connectionId = Context.ConnectionId;
        Console.WriteLine($"CommandHub: client disconnected: {connectionId} (reason: {exception?.Message ?? "none"})");

        await base.OnDisconnectedAsync(exception);
    }

    public async Task UpdateStatus(string macAddress, string status)
    {
        if (string.IsNullOrWhiteSpace(macAddress) || string.IsNullOrWhiteSpace(status))
        {
            return;
        }

        Console.WriteLine($"CommandHub: UpdateStatus called - MAC: {macAddress}, Status: {status}");

        var device = await _dbContext.Devices
            .FirstOrDefaultAsync(d => d.MacAddress.ToUpper() == macAddress.ToUpper());
        if (device == null)
        {
            Console.WriteLine($"CommandHub: Device not found for MAC: {macAddress}, creating new device");
            device = new Device
            {
                MacAddress = macAddress,
                Hostname = $"Device-{macAddress.Replace("-", "").Substring(0, 6)}",
                IpAddress = Context.GetHttpContext()?.Connection.RemoteIpAddress?.ToString() ?? "Unknown",
                Status = status
            };
            _dbContext.Devices.Add(device);
        }
        else
        {
            device.Status = status;
        }

        await _dbContext.SaveChangesAsync();

        await Clients.All.SendAsync("DeviceStatusChanged", macAddress, status);
    }

    public async Task SendCommand(string targetMac, string action, object payload)
    {
        var commandEnvelope = new
        {
            targetMac,
            action,
            payload,
            timestamp = DateTime.UtcNow
        };

        await Clients.All.SendAsync("ReceiveCommand", commandEnvelope);
    }
}

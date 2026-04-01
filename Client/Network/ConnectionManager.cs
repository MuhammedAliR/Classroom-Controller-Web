using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.SignalR.Client;
using ClassroomController.Client.Security;
using ClassroomController.Client.Utils;

namespace ClassroomController.Client.Network;

public class ConnectionManager
{
    private string _realMacAddress;
    private string _realMacAddressNoDashes;
    private HubConnection? _hubConnection;
    private ClientWebSocket? _webSocket;
    private readonly RegistryManager _registryManager = new();
    private readonly ConfigService _configService;

    public ConnectionManager(ConfigService configService)
    {
        _configService = configService;
        _realMacAddress = NetworkUtils.GetActiveMacAddress();
        _realMacAddressNoDashes = _realMacAddress.Replace("-", "");
        Logger.Log($"MAC Address: {_realMacAddress}");
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // SignalR connection
        var hubUrl = $"{_configService.ServerUrl}/commandhub";
        Logger.Log($"Attempting to connect to SignalR at {hubUrl}...");
        _hubConnection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect()
            .Build();

        _hubConnection.On<string>("LockScreen", (targetMac) =>
        {
            if (targetMac == _realMacAddress)
            {
                // Implement lock screen logic here
                Console.WriteLine("Locking screen");
            }
        });

        _hubConnection.On<string>("UnlockScreen", (targetMac) =>
        {
            if (targetMac == _realMacAddress)
            {
                // Implement unlock screen logic here
                Console.WriteLine("Unlocking screen");
            }
        });

        _hubConnection.On<string>("ToggleAdminMode", (targetMac) =>
        {
            if (targetMac == _realMacAddress)
            {
                // Toggle admin mode
                _registryManager.SetAdminMode(false); // Example: disable
                Console.WriteLine("Toggling admin mode");
            }
        });

        try
        {
            await _hubConnection.StartAsync(cancellationToken);
            Logger.Log("SignalR Connected Successfully.");
        }
        catch (Exception ex)
        {
            Logger.Log($"SignalR Connection Failed: {ex.Message}");
            throw;
        }

        // Update status to Online
        Logger.Log($"Sending UpdateStatus for MAC: {_realMacAddress}");
        await _hubConnection.InvokeAsync("UpdateStatus", _realMacAddress, "Online");

        // WebSocket connection
        var wsUrl = $"{_configService.ServerUrl.Replace("http", "ws")}/ws/stream?role=student&mac={_realMacAddress}";
        _webSocket = new ClientWebSocket();
        await _webSocket.ConnectAsync(new Uri(wsUrl), cancellationToken);
    }

    public async Task StopAsync()
    {
        if (_hubConnection != null)
        {
            await _hubConnection.StopAsync();
        }
        if (_webSocket != null)
        {
            await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client stopping", CancellationToken.None);
        }
    }

    public async Task SendVideoFrame(byte[] jpegBytes)
    {
        if (_webSocket?.State != WebSocketState.Open) return;

        var macBytes = Encoding.ASCII.GetBytes(_realMacAddressNoDashes.PadRight(12, '\0'));
        var payload = macBytes.Concat(jpegBytes).ToArray();
        await _webSocket.SendAsync(payload, WebSocketMessageType.Binary, true, CancellationToken.None);
    }
}

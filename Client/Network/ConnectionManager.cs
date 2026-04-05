using System.Net.WebSockets;
using System.Text;
using System.Windows;
using System.Globalization;
using Microsoft.AspNetCore.SignalR.Client;
using ClassroomController.Client.Utils;
using ClassroomController.Client.Execution;

namespace ClassroomController.Client.Network;

public class ConnectionManager
{
    private string _realMacAddress;
    private string _realMacAddressNoDashes;
    private HubConnection? _hubConnection;
    private ClientWebSocket? _webSocket;
    private bool _isStopping = false;
    private bool _hasLoggedWebSocketUnavailable = false;
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
        var hubUrl =
            $"{_configService.ServerUrl}/commandhub?key={Uri.EscapeDataString(_configService.MasterKey)}&mac={Uri.EscapeDataString(_realMacAddress)}";
        Logger.Log($"Attempting to connect to SignalR at {hubUrl}...");
        _hubConnection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect()
            .Build();

        _hubConnection.On("ReceiveLock", () =>
        {
            Logger.Log("ReceiveLock received.");
            System.Windows.Application.Current.Dispatcher.Invoke(SystemController.LockScreen);
        });

        _hubConnection.On("ReceiveUnlock", () =>
        {
            Logger.Log("ReceiveUnlock received.");
            System.Windows.Application.Current.Dispatcher.Invoke(SystemController.UnlockScreen);
        });

        // Register listener for command execution (replaces old handlers above)
        _hubConnection.On<string, string>("ExecuteCommand", (action, payload) =>
        {
            Logger.Log($"ExecuteCommand received: action={action}, payload={payload}");
            HandleCommand(action, payload);
        });

        _hubConnection.On<string, double, double, int>("ReceiveMouseInput", (eventType, xPct, yPct, button) =>
        {
            SystemController.HandleMouseInput(eventType, xPct, yPct, button);
        });

        _hubConnection.On<string, int>("ReceiveKeyboardInput", (eventType, keyCode) =>
        {
            SystemController.HandleKeyboardInput(eventType, keyCode);
        });

        _hubConnection.On<bool>("ReceiveFreezeUpdate", isFrozen =>
        {
            Logger.Log($"ReceiveFreezeUpdate received: isFrozen={isFrozen}");
            if (isFrozen)
            {
                SystemController.StartSoftLock();
            }
            else
            {
                SystemController.StopSoftLock();
            }
        });

        _hubConnection.On<string?>("ReceiveStartTimer", timerEndTime =>
        {
            Logger.Log($"ReceiveStartTimer received: timerEndTime={timerEndTime}");
            if (DateTime.TryParse(timerEndTime, null, DateTimeStyles.RoundtripKind, out var parsedTimerEnd))
            {
                SystemController.StartTimer(parsedTimerEnd.ToUniversalTime() - DateTime.UtcNow);
            }
            else
            {
                Logger.Log($"ReceiveStartTimer ignored invalid timerEndTime '{timerEndTime}'");
            }
        });

        _hubConnection.On("ReceiveStopTimer", () =>
        {
            Logger.Log("ReceiveStopTimer received.");
            SystemController.StopTimer();
        });

        _hubConnection.On<bool, bool, bool, DateTime?, string>("SyncState",
            (isLocked, isFrozen, isAdminMode, timerEndTime, blockedWebsites) =>
            {
                Logger.Log(
                    $"SyncState received: lock={isLocked}, freeze={isFrozen}, admin={isAdminMode}, timerEnd={timerEndTime?.ToString("O") ?? "null"}, blockedWebsites={blockedWebsites}");
                SystemController.ApplySyncState(isLocked, isFrozen, isAdminMode, timerEndTime, blockedWebsites);
            });

        _hubConnection.On<string>("SetStreamQuality", (quality) =>
        {
            if (string.Equals(quality, "high", StringComparison.OrdinalIgnoreCase))
            {
                SystemController.SetStreamModeHigh();
            }
            else
            {
                SystemController.SetStreamModeLow();
            }
        });

        _hubConnection.On<bool>("ReceiveAdminModeUpdate", enableAdmin =>
        {
            Logger.Log($"ReceiveAdminModeUpdate received: enableAdmin={enableAdmin}");
            SystemController.EnforceStudentMode(!enableAdmin);
        });

        _hubConnection.On<string, bool>("ReceiveWebsiteBlock", (domain, block) =>
        {
            Logger.Log($"ReceiveWebsiteBlock received: domain={domain}, block={block}");
            SystemController.HandleWebsiteBlock(domain, block);
        });

        _hubConnection.Reconnecting += error =>
        {
            Logger.Log($"SignalR reconnecting due to error: {error?.Message}");
            return Task.CompletedTask;
        };

        _hubConnection.Reconnected += async connectionId =>
        {
            Logger.Log($"SignalR reconnected with connectionId: {connectionId}");
            if (_hubConnection != null)
            {
                try
                {
                    Logger.Log($"Resending UpdateStatus for MAC: {_realMacAddress} after reconnect");
                    await _hubConnection.InvokeAsync("UpdateStatus", _realMacAddress, "Online");
                }
                catch (Exception ex)
                {
                    Logger.Log($"Failed to resend UpdateStatus after reconnect: {ex.Message}");
                }
            }
        };

        _hubConnection.Closed += async error =>
        {
            Logger.Log($"SignalR closed: {error?.Message}. Attempting restart in 5s...");
            if (!_isStopping)
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                try
                {
                    if (_hubConnection != null)
                    {
                        await _hubConnection.StartAsync();
                        Logger.Log("SignalR reconnected successfully after close.");
                        await _hubConnection.InvokeAsync("UpdateStatus", _realMacAddress, "Online");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"SignalR restart failed after close: {ex.Message}");
                }
            }
        };

        // Initial connection with retry loop
        while (true)
        {
            try
            {
                await _hubConnection.StartAsync(cancellationToken);
                Logger.Log("SignalR Connected Successfully.");
                break; // Exit retry loop on success
            }
            catch (Exception ex)
            {
                Logger.Log($"Server unreachable, retrying in 5 seconds... Error: {ex.Message}");
                await Task.Delay(5000, cancellationToken);
            }
        }

        // Update status to Online
        Logger.Log($"Sending UpdateStatus for MAC: {_realMacAddress}");
        await _hubConnection.InvokeAsync("UpdateStatus", _realMacAddress, "Online");

        // WebSocket will be connected on-demand in SendVideoFrame
    }

    public async Task StopAsync()
    {
        _isStopping = true;

        try
        {
            if (_hubConnection != null)
            {
                if (_hubConnection.State == HubConnectionState.Connected)
                {
                    try
                    {
                        await _hubConnection.InvokeAsync("UpdateStatus", _realMacAddress, "Offline");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Failed to send Offline status during shutdown: {ex.Message}");
                    }
                }

                await _hubConnection.StopAsync();
            }
            if (_webSocket != null)
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client stopping", CancellationToken.None);
            }
        }
        finally
        {
            SystemController.RestoreLockPoliciesAndInput();
            SystemController.EnforceStudentMode(false);
        }
    }

    public async Task SendVideoFrame(byte[] jpegBytes)
    {
        await EnsureWebSocketConnectedAsync();
        
        if (_webSocket?.State != WebSocketState.Open)
        {
            if (!_hasLoggedWebSocketUnavailable)
            {
                Logger.Log("WebSocket unavailable, pausing frame delivery until reconnect succeeds.");
                _hasLoggedWebSocketUnavailable = true;
            }
            return;
        }

        var macBytes = Encoding.ASCII.GetBytes(_realMacAddressNoDashes.PadRight(12, '\0'));
        var payload = macBytes.Concat(jpegBytes).ToArray();

        try
        {
            await _webSocket.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Binary, true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Logger.Log($"SendVideoFrame failed: {ex.Message}");
            DisposeWebSocket();
            throw;
        }
    }

    private async Task EnsureWebSocketConnectedAsync()
    {
        if (_webSocket == null || _webSocket.State != WebSocketState.Open)
        {
            // Dispose old websocket if it exists
            if (_webSocket != null && _webSocket.State != WebSocketState.Open)
            {
                try
                {
                    if (_webSocket.State == WebSocketState.Open || _webSocket.State == WebSocketState.Connecting)
                    {
                        await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Reconnecting", CancellationToken.None);
                    }
                }
                catch
                {
                    // Ignore close errors
                }
                DisposeWebSocket();
                Logger.Log("WebSocket disposed for reconnection");
            }

            // Create new websocket
            _webSocket = new ClientWebSocket();
            var wsUrl = $"{_configService.ServerUrl.Replace("http", "ws")}/ws/stream?role=student&mac={Uri.EscapeDataString(_realMacAddress)}&key={Uri.EscapeDataString(_configService.MasterKey)}";

            try
            {
                Logger.Log($"Attempting WebSocket connection to {wsUrl}");
                using var cts = new System.Threading.CancellationTokenSource(5000); // 5 second timeout
                await _webSocket.ConnectAsync(new Uri(wsUrl), cts.Token);
                _hasLoggedWebSocketUnavailable = false;
                Logger.Log("WebSocket connected successfully");
            }
            catch (OperationCanceledException)
            {
                Logger.Log("WebSocket connection timed out after 5 seconds");
                DisposeWebSocket();
            }
            catch (Exception ex)
            {
                Logger.Log($"WebSocket connection failed: {ex.Message}");
                DisposeWebSocket();
                // Don't throw - just log and return, SendVideoFrame will skip
            }
        }
    }

    private void DisposeWebSocket()
    {
        try
        {
            _webSocket?.Dispose();
        }
        catch
        {
            // Ignore dispose errors during recovery.
        }

        _webSocket = null;
    }

    private void HandleCommand(string action, string payload)
    {
        Logger.Log($"HandleCommand: action={action}");
        
        switch (action.ToLower())
        {
            case "lock":
                // Dispatch to UI thread
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    SystemController.LockScreen();
                });
                break;

            case "unlock":
                // Dispatch to UI thread
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    SystemController.UnlockScreen();
                });
                break;

            case "reboot":
                Logger.Log("HandleCommand: Executing reboot");
                SystemController.Reboot();
                break;

            case "poweroff":
                Logger.Log("HandleCommand: Executing power off");
                SystemController.PowerOff();
                break;

            case "message":
                // Dispatch to UI thread
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    SystemController.ShowMessage(payload);
                });
                break;

            case "startsoftlock":
                SystemController.StartSoftLock();
                break;

            case "stopsoftlock":
                SystemController.StopSoftLock();
                break;

            case "starttimer":
                if (int.TryParse(payload, out var minutes) && minutes > 0)
                {
                    SystemController.StartTimer(minutes);
                }
                else if (DateTime.TryParse(payload, null, DateTimeStyles.RoundtripKind, out var parsedTimerEnd))
                {
                    SystemController.StartTimer(parsedTimerEnd.ToUniversalTime() - DateTime.UtcNow);
                }
                else
                {
                    Logger.Log($"HandleCommand: Invalid timer payload '{payload}'");
                }
                break;

            case "stoptimer":
                SystemController.StopTimer();
                break;

            default:
                Logger.Log($"HandleCommand: Unknown action '{action}'");
                break;
        }
    }
}

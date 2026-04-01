using ClassroomController.Client.Media;
using ClassroomController.Client.Network;

namespace ClassroomController.Client;

public class ClientEngine
{
    private readonly ConnectionManager _connectionManager;
    private readonly ScreenCapturer _screenCapturer;
    private CancellationTokenSource? _cts;

    public ClientEngine(ConfigService configService)
    {
        _connectionManager = new ConnectionManager(configService);
        _screenCapturer = new ScreenCapturer(_connectionManager);
    }

    public async Task StartAsync()
    {
        _cts = new CancellationTokenSource();

        // Start connections
        await _connectionManager.StartAsync(_cts.Token);

        // Start screen capture loop
        _ = _screenCapturer.StartCaptureLoopAsync(_cts.Token);
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        await _connectionManager.StopAsync();
    }
}

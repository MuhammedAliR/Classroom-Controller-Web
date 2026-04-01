using System.Windows;

namespace ClassroomController.Client;

public partial class App : System.Windows.Application
{
    private ClientEngine? _clientEngine;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Load configuration
        var configService = new ConfigService();

        // Start the client engine in the background
        _clientEngine = new ClientEngine(configService);
        _clientEngine.StartAsync().ConfigureAwait(false);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _clientEngine?.StopAsync().ConfigureAwait(false);
        base.OnExit(e);
    }
}

using System.Windows;
using ClassroomController.Client.Execution;

namespace ClassroomController.Client;

public partial class App : System.Windows.Application
{
    private ClientEngine? _clientEngine;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        SystemController.RestoreLockPoliciesAndInput();

        // Load configuration
        var configService = new ConfigService();

        // Start the client engine in the background
        _clientEngine = new ClientEngine(configService);
        _ = _clientEngine.StartAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            SystemController.RestoreLockPoliciesAndInput();
            _clientEngine?.StopAsync().GetAwaiter().GetResult();
        }
        finally
        {
            SystemController.RestoreLockPoliciesAndInput();
            base.OnExit(e);
        }
    }
}

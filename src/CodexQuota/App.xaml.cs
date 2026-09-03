using System.Configuration;
using System.Data;
using System.Windows;

namespace CodexQuota;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private Mutex? _instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        var instanceMutex = new Mutex(true, "CodexQuota.Companion", out var createdNew);
        if (!createdNew)
        {
            instanceMutex.Dispose();
            Shutdown();
            return;
        }

        _instanceMutex = instanceMutex;
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}

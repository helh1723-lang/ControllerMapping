using System.Windows;

namespace GameControllerMapper;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            Environment.Exit(SelfTest.Run());
            return;
        }

        if (e.Args.Contains("--native-input-test", StringComparer.OrdinalIgnoreCase))
        {
            Environment.Exit(SelfTest.RunNativeInput());
            return;
        }

        if (e.Args.Contains("--gameinput-test", StringComparer.OrdinalIgnoreCase))
        {
            Environment.Exit(SelfTest.RunGameInput());
            return;
        }

        if (e.Args.Contains("--gameinput-device-test", StringComparer.OrdinalIgnoreCase))
        {
            Environment.Exit(SelfTest.RunGameInputDevice());
            return;
        }

        if (e.Args.Contains("--gameinput-thread-test", StringComparer.OrdinalIgnoreCase))
        {
            Environment.Exit(SelfTest.RunGameInputThread());
            return;
        }

        MainWindow = new MainWindow();
        MainWindow.Show();
    }
}

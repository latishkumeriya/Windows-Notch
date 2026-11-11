using System.Windows;

namespace NotchWin;

public partial class App : Application
{
    Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new Mutex(true, @"Local\NotchWin.SingleInstance", out bool created);
        if (!created)
        {
            Shutdown();
            return;
        }

        // A notch should never disappear because one widget hit a bad state.
        DispatcherUnhandledException += (_, args) =>
        {
            Log(args.Exception);
            args.Handled = true;
        };

        base.OnStartup(e);
        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    static void Log(Exception ex)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NotchWin");
            Directory.CreateDirectory(dir);
            var log = Path.Combine(dir, "error.log");
            // Keep the log bounded so a repeating error can't fill the disk.
            if (File.Exists(log) && new FileInfo(log).Length > 512 * 1024) File.Delete(log);
            File.AppendAllText(log, $"[{DateTime.Now:u}] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n\n");
        }
        catch { }
    }
}

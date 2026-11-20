using System.Windows;
using Grab.Services;

namespace Grab;

public partial class App : Application
{
    Mutex _mutex;
    bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (!AppPaths.EnsureDataDir(out var error))
        {
            MessageBox.Show("Grab can't write to its own folder:\n" + AppPaths.BaseDir + "\n\n" + error +
                            "\n\nMove the app to a folder you can write to (like your Desktop or Documents).",
                "Grab", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        try { if (Directory.Exists(AppPaths.TempDir)) Directory.Delete(AppPaths.TempDir, true); } catch { }

        SettingsStore.Load();

        _mutex = new Mutex(true, "Grab.SingleInstance", out var created);
        _ownsMutex = created;
        if (!created)
        {
            MessageBox.Show("Grab is already running.", "Grab", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        new MainWindow().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_mutex != null)
            {
                if (_ownsMutex) _mutex.ReleaseMutex();
                _mutex.Dispose();
            }
        }
        catch
        {
            // ignore
        }
        base.OnExit(e);
    }
}

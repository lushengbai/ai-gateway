using System.Windows;
using AiGateway.UI;

namespace AiGateway;

/// <summary>
/// Application entry point. Creates the main window and the system-tray icon, and
/// centralizes the shutdown sequence. Closing the window only hides it to the tray
/// (see <see cref="MainWindow.OnClosing"/>); the app truly exits only via the tray's
/// Exit item (or a Windows session end), routed through <see cref="ExitApplication"/>.
/// </summary>
public partial class App : System.Windows.Application
{
    private MainWindow _window = null!;
    private MainViewModel _viewModel = null!;
    private TrayIconController _tray = null!;
    private bool _isExiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _window = new MainWindow();
        _viewModel = (MainViewModel)_window.DataContext;
        _tray = new TrayIconController(_window, _viewModel, ExitApplication);
        _window.HiddenToTray += (_, _) => _tray.ShowHiddenHint();

        _window.Show();
    }

    /// <summary>
    /// The single real-exit path: stop the proxy and persist config, remove the tray
    /// icon, then let the window actually close (which shuts down the app under the
    /// default OnLastWindowClose mode). Idempotent.
    /// </summary>
    private void ExitApplication()
    {
        if (_isExiting)
            return;
        _isExiting = true;

        _viewModel.Shutdown();   // stop proxy backend, persist configuration
        _tray.Dispose();         // remove the tray icon
        _window.ExitForReal();   // bypass hide-to-tray and close for real
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // Windows is logging off / shutting down: exit cleanly instead of blocking it.
        base.OnSessionEnding(e);
        ExitApplication();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();   // safety net if the app ever exits via another path
        base.OnExit(e);
    }
}

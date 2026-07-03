using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace AiGateway.UI;

/// <summary>
/// Owns the system-tray icon (WinForms <see cref="Forms.NotifyIcon"/>) and its
/// right-click menu, bridging tray interactions to the window and view model:
/// restore the window, start/stop the proxy, and exit the application.
/// The Start/Stop item states and the tooltip track <see cref="MainViewModel.IsRunning"/>.
/// </summary>
/// <remarks>
/// Created on the WPF UI thread (which supplies the message pump NotifyIcon needs).
/// WinForms and System.Drawing types are referenced through the <c>Forms</c> and
/// <c>Drawing</c> aliases; their implicit global usings are suppressed in the csproj
/// to avoid clashing with WPF types of the same name.
/// </remarks>
public sealed class TrayIconController : IDisposable
{
    private static readonly Uri IconUri = new("pack://application:,,,/Resources/app.ico");

    private readonly Window _window;
    private readonly MainViewModel _viewModel;
    private readonly Action _exit;

    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ToolStripMenuItem _startItem;
    private readonly Forms.ToolStripMenuItem _stopItem;

    private bool _hintShown;
    private bool _disposed;

    public TrayIconController(Window window, MainViewModel viewModel, Action exit)
    {
        _window = window;
        _viewModel = viewModel;
        _exit = exit;

        var openItem = new Forms.ToolStripMenuItem("Open", null, (_, _) => RestoreWindow());
        openItem.Font = new Drawing.Font(openItem.Font, Drawing.FontStyle.Bold); // default (double-click) action
        _startItem = new Forms.ToolStripMenuItem("Start", null, (_, _) => TryExecute(_viewModel.StartCommand));
        _stopItem = new Forms.ToolStripMenuItem("Stop", null, (_, _) => TryExecute(_viewModel.StopCommand));
        var exitItem = new Forms.ToolStripMenuItem("Exit", null, (_, _) => _exit());

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(openItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(_startItem);
        menu.Items.Add(_stopItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Visible = true,
            ContextMenuStrip = menu,
        };
        _notifyIcon.DoubleClick += (_, _) => RestoreWindow();

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        UpdateRunningState();
    }

    /// <summary>Loads the tray icon at the system's small-icon size for crisp rendering at any DPI.</summary>
    private static Drawing.Icon LoadTrayIcon()
    {
        var info = System.Windows.Application.GetResourceStream(IconUri)
                   ?? throw new InvalidOperationException($"Tray icon resource not found: {IconUri}");
        using var stream = info.Stream;
        return new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsRunning) || e.PropertyName == nameof(MainViewModel.Port))
            UpdateRunningState();
    }

    private void UpdateRunningState()
    {
        bool running = _viewModel.IsRunning;
        _startItem.Enabled = !running;
        _stopItem.Enabled = running;
        // NotifyIcon.Text is capped at 63 chars; keep this short.
        _notifyIcon.Text = running
            ? $"AI Gateway — running :{_viewModel.Port}"
            : "AI Gateway — stopped";
    }

    private static void TryExecute(ICommand command)
    {
        if (command.CanExecute(null))
            command.Execute(null);
    }

    /// <summary>Restore and focus the main window (from double-click or the Open menu item).</summary>
    private void RestoreWindow()
    {
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Activate();
        // Force the window to the foreground from a background (tray-only) process.
        _window.Topmost = true;
        _window.Topmost = false;
    }

    /// <summary>Show a one-time balloon hint the first time the window is hidden to the tray.</summary>
    public void ShowHiddenHint()
    {
        if (_hintShown || _disposed)
            return;
        _hintShown = true;
        _notifyIcon.BalloonTipTitle = "AI Gateway is still running";
        _notifyIcon.BalloonTipText = "The proxy keeps running in the background. Right-click the tray icon to exit.";
        _notifyIcon.ShowBalloonTip(3000);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _notifyIcon.Visible = false;   // remove the icon immediately, before disposal
        _notifyIcon.Dispose();
    }
}

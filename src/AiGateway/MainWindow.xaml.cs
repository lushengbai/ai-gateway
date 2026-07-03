using System.ComponentModel;
using System.Windows;
using AiGateway.UI;

namespace AiGateway;

public partial class MainWindow : Window
{
    private bool _forceClose;

    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Raised when the user closes the window while the app is not exiting — i.e. the
    /// window was hidden to the tray rather than closed. Lets the app show a one-time hint.
    /// </summary>
    public event EventHandler? HiddenToTray;

    /// <summary>Close the window for real, bypassing the hide-to-tray behavior in <see cref="OnClosing"/>.</summary>
    public void ExitForReal()
    {
        _forceClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // A real exit (tray "Exit" or session end) has already torn down the backend
        // in App.ExitApplication; just let the window close.
        if (_forceClose)
        {
            base.OnClosing(e);
            return;
        }

        // Otherwise the X button only hides the window to the tray; the proxy keeps
        // running in the background. Exit is available from the tray's right-click menu.
        e.Cancel = true;
        Hide();
        HiddenToTray?.Invoke(this, EventArgs.Empty);
    }
}

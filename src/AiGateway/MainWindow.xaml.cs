using System.ComponentModel;
using System.Windows;
using AiGateway.UI;

namespace AiGateway;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.Shutdown();
        base.OnClosing(e);
    }
}

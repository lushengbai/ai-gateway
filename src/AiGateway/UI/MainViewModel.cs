using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AiGateway.Config;
using AiGateway.Core;
using AiGateway.Logging;

namespace AiGateway.UI;

/// <summary>
/// Main window view model. Owns the active proxy backend (reverse or forward),
/// exposes bindable configuration (port, mode, upstream proxy, routes), and a
/// filtered view over the log entries.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppConfig _config;
    private readonly LogService _logService;
    private IProxyBackend? _backend;

    public const string AllProviders = "All";

    public MainViewModel()
    {
        _config = AppConfig.Load();
        _logService = new LogService();

        _port = _config.Port;
        _mode = _config.Mode;
        _onlyLogAiRequests = _config.OnlyLogAiRequests;

        _upstreamEnabled = _config.UpstreamProxy.Enabled;
        _upstreamHost = _config.UpstreamProxy.Host;
        _upstreamPort = _config.UpstreamProxy.Port;
        _upstreamUsername = _config.UpstreamProxy.Username;
        _upstreamPassword = _config.UpstreamProxy.Password;

        Routes = new ObservableCollection<ApiRoute>(_config.Routes);

        LogView = CollectionViewSource.GetDefaultView(_logService.Entries);
        LogView.Filter = FilterLogEntry;

        ProviderFilters = new ObservableCollection<string>();
        RefreshProviderFilters();
    }

    public ICollectionView LogView { get; }

    public ObservableCollection<ApiRoute> Routes { get; }

    public ObservableCollection<string> ProviderFilters { get; }

    public ProxyMode[] Modes { get; } = { ProxyMode.ReverseProxy, ProxyMode.ForwardProxy };

    [ObservableProperty]
    private int _port;

    [ObservableProperty]
    private ProxyMode _mode;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _isRunning;

    [ObservableProperty]
    private string _statusText = "Stopped";

    [ObservableProperty]
    private string _lastMessage = "";

    [ObservableProperty]
    private bool _onlyLogAiRequests;

    [ObservableProperty]
    private bool _upstreamEnabled;

    [ObservableProperty]
    private string _upstreamHost;

    [ObservableProperty]
    private int _upstreamPort;

    [ObservableProperty]
    private string _upstreamUsername;

    [ObservableProperty]
    private string _upstreamPassword;

    [ObservableProperty]
    private ApiRoute? _selectedRoute;

    [ObservableProperty]
    private string _selectedProvider = AllProviders;

    partial void OnSelectedProviderChanged(string value) => LogView.Refresh();

    private bool FilterLogEntry(object obj)
    {
        if (obj is not LogEntry entry)
            return false;
        if (SelectedProvider == AllProviders)
            return true;
        return string.Equals(entry.Provider, SelectedProvider, StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshProviderFilters()
    {
        var current = SelectedProvider;
        ProviderFilters.Clear();
        ProviderFilters.Add(AllProviders);

        foreach (var name in Routes.Select(r => r.Name)
                                   .Concat(_config.Targets.Select(t => t.Name))
                                   .Where(n => !string.IsNullOrWhiteSpace(n))
                                   .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ProviderFilters.Add(name);
        }

        SelectedProvider = ProviderFilters.Contains(current) ? current : AllProviders;
    }

    private void ApplyToConfig()
    {
        _config.Port = Port;
        _config.Mode = Mode;
        _config.OnlyLogAiRequests = OnlyLogAiRequests;

        _config.UpstreamProxy.Enabled = UpstreamEnabled;
        _config.UpstreamProxy.Host = UpstreamHost ?? "";
        _config.UpstreamProxy.Port = UpstreamPort;
        _config.UpstreamProxy.Username = UpstreamUsername ?? "";
        _config.UpstreamProxy.Password = UpstreamPassword ?? "";

        _config.Routes = Routes.ToList();
    }

    private bool CanStart() => !IsRunning;
    private bool CanStop() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Start()
    {
        try
        {
            ApplyToConfig();
            _config.Save();
            RefreshProviderFilters();

            _backend = Mode == ProxyMode.ReverseProxy
                ? new ReverseProxyService(_config, _logService)
                : new ForwardProxyService(_config, _logService);

            _backend.Message += OnProxyMessage;
            _backend.Start(Port);

            IsRunning = _backend.IsRunning;
            var modeText = Mode == ProxyMode.ReverseProxy ? "Reverse" : "Forward";
            StatusText = IsRunning ? $"Running · {modeText} · 127.0.0.1:{Port}" : "Stopped";
        }
        catch (Exception ex)
        {
            TeardownBackend();
            IsRunning = false;
            StatusText = "Stopped";
            LastMessage = $"Failed to start: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        TeardownBackend();
        IsRunning = false;
        StatusText = "Stopped";
    }

    [RelayCommand]
    private void AddRoute()
    {
        var route = new ApiRoute { Name = "NewApi", PathPrefix = "/new", TargetBaseUrl = "https://", Enabled = true };
        Routes.Add(route);
        SelectedRoute = route;
    }

    [RelayCommand]
    private void RemoveRoute()
    {
        if (SelectedRoute is not null)
            Routes.Remove(SelectedRoute);
    }

    [RelayCommand]
    private void ClearLog() => _logService.Clear();

    private void TeardownBackend()
    {
        if (_backend is null)
            return;
        _backend.Message -= OnProxyMessage;
        _backend.Stop();
        _backend.Dispose();
        _backend = null;
    }

    public void Shutdown()
    {
        TeardownBackend();
        ApplyToConfig();
        _config.Save();
    }

    private void OnProxyMessage(string message) => LastMessage = message;
}

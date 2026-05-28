using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SalesLedger.Core.Services;
using Velopack;
using Velopack.Sources;

namespace SalesLedger.Core.ViewModels
{
    public partial class MainWindowViewModel : ObservableObject, IDisposable
    {
        // Core Services
        public LiteDbService LiteDb { get; }
        public DuckDbService DuckDb { get; }
        public SyncPipeline Sync { get; }
        public PayoutLedgerService PayoutService { get; }
        public AnalyticsService Analytics { get; }
        public ReportGenerator ReportGen { get; }
        public CommissionProcessor CommissionProc { get; }

        // Workspaces
        public LedgerDashboardViewModel DashboardViewModel { get; }
        public FullScreenSettingsViewModel SettingsViewModel { get; }

        [ObservableProperty]
        private ObservableObject _currentActiveWorkspace;

        [ObservableProperty]
        private string _settingsButtonText = "Settings Panel";

        public MainWindowViewModel()
        {
            // Initialize operational store
            LiteDb = new LiteDbService();

            // Initialize DuckDB OLAP store
            DuckDb = new DuckDbService();

            // Setup channels pipeline
            Sync = new SyncPipeline(LiteDb, DuckDb);

            // Wire domain dependencies
            PayoutService = new PayoutLedgerService(LiteDb, Sync);
            Analytics = new AnalyticsService(DuckDb);
            ReportGen = new ReportGenerator();
            CommissionProc = new CommissionProcessor();

            // Setup Workspaces
            DashboardViewModel = new LedgerDashboardViewModel(this);
            SettingsViewModel = new FullScreenSettingsViewModel(this);

            _currentActiveWorkspace = DashboardViewModel;

            // Subscribe and start the background sync pipeline
            Sync.SyncCompleted += DashboardViewModel.OnSyncCompleted;
            Sync.Start();

            // Run update check in background task
            Task.Run(CheckForApplicationUpdatesAsync);
        }

        [RelayCommand]
        public void ToggleWorkspace()
        {
            if (CurrentActiveWorkspace == DashboardViewModel)
            {
                SettingsViewModel.LoadSettings();
                CurrentActiveWorkspace = SettingsViewModel;
                SettingsButtonText = "Dashboard Panel";
            }
            else
            {
                CurrentActiveWorkspace = DashboardViewModel;
                SettingsButtonText = "Settings Panel";
            }
        }

        public void Dispose()
        {
            Sync.SyncCompleted -= DashboardViewModel.OnSyncCompleted;
            Sync.Stop();
            LiteDb.Dispose();
            DuckDb.Dispose();
        }

        private async Task CheckForApplicationUpdatesAsync()
        {
            try
            {
                var settings = LiteDb.GetUserSettings();
                if (!settings.AutoUpdateEnabled)
                {
                    return;
                }

                var updateSource = new GithubSource("https://github.com/jbwinn/spiffs-dashboard", null, false);
                UpdateManager = new UpdateManager(updateSource);
                UpdateInfo = await UpdateManager.CheckForUpdatesAsync();
                
                if (UpdateInfo != null)
                {
                    UpdateMessageText = $"A new version (v{UpdateInfo.TargetFullRelease.Version}) is available. Would you like to install it now? The application will restart automatically.";
                    IsUpdateDialogVisible = true;
                }
            }
            catch (Exception)
            {
                // Fail silently to safeguard application operations if the host machine goes offline
            }
        }

        private bool _isUpdateDialogVisible;
        public bool IsUpdateDialogVisible
        {
            get => _isUpdateDialogVisible;
            set => SetProperty(ref _isUpdateDialogVisible, value);
        }

        private string _updateMessageText = string.Empty;
        public string UpdateMessageText
        {
            get => _updateMessageText;
            set => SetProperty(ref _updateMessageText, value);
        }

        private bool _isUpdateInstalling;
        public bool IsUpdateInstalling
        {
            get => _isUpdateInstalling;
            set
            {
                if (SetProperty(ref _isUpdateInstalling, value))
                {
                    OnPropertyChanged(nameof(IsNotUpdateInstalling));
                }
            }
        }

        public bool IsNotUpdateInstalling => !_isUpdateInstalling;

        public UpdateManager? UpdateManager { get; set; }
        public UpdateInfo? UpdateInfo { get; set; }

        private IRelayCommand? _closeUpdateDialogCommand;
        public IRelayCommand CloseUpdateDialogCommand => 
            _closeUpdateDialogCommand ??= new RelayCommand(() => IsUpdateDialogVisible = false);

        private IAsyncRelayCommand? _installUpdateCommand;
        public IAsyncRelayCommand InstallUpdateCommand => 
            _installUpdateCommand ??= new AsyncRelayCommand(InstallUpdateAsync);

        public async Task InstallUpdateAsync()
        {
            if (IsUpdateInstalling) return;
            IsUpdateInstalling = true;
            UpdateMessageText = "Downloading and applying update... Please wait.";

            try
            {
                if (UpdateManager == null || UpdateInfo == null)
                {
                    // Mock update simulation for testing/validation of the UI
                    await Task.Delay(2000);
                    UpdateMessageText = "Update installed! Restarting application (Mock)...";
                    await Task.Delay(1500);
                    IsUpdateDialogVisible = false;
                    IsUpdateInstalling = false;
                }
                else
                {
                    await UpdateManager.DownloadUpdatesAsync(UpdateInfo);
                    UpdateMessageText = "Update installed! Restarting application...";
                    await Task.Delay(1500);
                    UpdateManager.ApplyUpdatesAndRestart(UpdateInfo);
                }
            }
            catch (Exception ex)
            {
                UpdateMessageText = $"Update installation failed: {ex.Message}";
                IsUpdateInstalling = false;
            }
        }
    }
}

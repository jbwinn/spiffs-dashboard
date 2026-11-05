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
        private DuckDbService DuckDb { get; }
        public SyncPipeline Sync { get; }
        public PayoutLedgerService PayoutService { get; }
        public AnalyticsService Analytics { get; }
        public ReportGenerator ReportGen { get; }
        public CommissionProcessor CommissionProc { get; }
        public ReceiptService Receipts { get; }
        public CsvImportService CsvImport { get; }
        public SkuCheckService SkuCheck { get; }
        public ReportService ReportService { get; }

        /// <summary>Opening, printing and copying files (replaced in tests so nothing real is launched).</summary>
        public IShellActions Shell { get; }

        // Workspaces
        private LedgerDashboardViewModel DashboardViewModel { get; }
        private ReportsViewModel ReportsViewModel { get; }
        private FullScreenSettingsViewModel SettingsViewModel { get; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsDashboardActive))]
        [NotifyPropertyChangedFor(nameof(IsReportsActive))]
        [NotifyPropertyChangedFor(nameof(IsSettingsActive))]
        private ObservableObject _currentActiveWorkspace;

        public bool IsDashboardActive => CurrentActiveWorkspace == DashboardViewModel;
        public bool IsReportsActive => CurrentActiveWorkspace == ReportsViewModel;
        public bool IsSettingsActive => CurrentActiveWorkspace == SettingsViewModel;

        public MainWindowViewModel() : this(dataDirectory: null, checkForUpdates: true)
        {
        }

        /// <summary>
        /// <paramref name="dataDirectory"/> overrides where the databases live (used by UI tests so they never touch
        /// real data); null uses the normal per-user location. <paramref name="shell"/> replaces the real
        /// open/print/copy actions (tests).
        /// </summary>
        public MainWindowViewModel(string? dataDirectory, bool checkForUpdates, IShellActions? shell = null)
        {
            // Initialize operational store and DuckDB OLAP store
            if (dataDirectory == null)
            {
                LiteDb = new LiteDbService();
                DuckDb = new DuckDbService();
            }
            else
            {
                LiteDb = new LiteDbService(System.IO.Path.Combine(dataDirectory, "salesledger.db"));
                DuckDb = new DuckDbService(System.IO.Path.Combine(dataDirectory, "analytics.duckdb"));
            }

            Shell = shell ?? new WindowsShellActions();

            // Setup channels pipeline
            Sync = new SyncPipeline(LiteDb, DuckDb);

            // Wire domain dependencies
            CommissionProc = new CommissionProcessor();
            PayoutService = new PayoutLedgerService(LiteDb, Sync, CommissionProc);
            Receipts = new ReceiptService(LiteDb, Sync, CommissionProc, PayoutService);
            CsvImport = new CsvImportService(LiteDb, Sync, CommissionProc);
            SkuCheck = new SkuCheckService(LiteDb, PayoutService);
            Analytics = new AnalyticsService(DuckDb);
            ReportGen = new ReportGenerator();
            ReportService = new ReportService(LiteDb, PayoutService, ReportGen);

            // Setup Workspaces
            DashboardViewModel = new LedgerDashboardViewModel(this);
            ReportsViewModel = new ReportsViewModel(this);
            SettingsViewModel = new FullScreenSettingsViewModel(this);

            _currentActiveWorkspace = DashboardViewModel;

            // Fix up return lines recorded before full-return tracking existed (the rebuild below then picks them up)
            PayoutService.RefreshReturnCompletionFlags();

            // Subscribe and start the background sync pipeline
            Sync.SyncCompleted += DashboardViewModel.OnSyncCompleted;
            Sync.Start();

            // Run update check in background task
            if (checkForUpdates)
            {
                Task.Run(CheckForApplicationUpdatesAsync);
            }
        }

        /// <summary>Header navigation: "Dashboard", "Reports" or "Settings".</summary>
        [RelayCommand]
        private void Navigate(string? destination)
        {
            switch (destination)
            {
                case "Reports":
                    ReportsViewModel.Refresh();
                    CurrentActiveWorkspace = ReportsViewModel;
                    break;
                case "Settings":
                    SettingsViewModel.LoadSettings();
                    CurrentActiveWorkspace = SettingsViewModel;
                    break;
                default:
                    // Settings may have changed categories, warranty types or rules; a finalize changes statuses
                    DashboardViewModel.Refresh();
                    CurrentActiveWorkspace = DashboardViewModel;
                    break;
            }
        }

        /// <summary>Called after the ledger changes outside the dashboard (e.g. a pay period was finalized).</summary>
        public void NotifyLedgerChanged() => DashboardViewModel.Refresh();

        /// <summary>True once shutting down: work queued for the UI thread (e.g. a refresh after a sync) must not touch the databases.</summary>
        public bool IsDisposed { get; private set; }

        public void Dispose()
        {
            IsDisposed = true;
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

        [ObservableProperty]
        public partial bool IsUpdateDialogVisible { get; set; }

        [ObservableProperty]
        public partial string UpdateMessageText { get; set; } = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsNotUpdateInstalling))]
        private partial bool IsUpdateInstalling { get; set; }

        public bool IsNotUpdateInstalling => !IsUpdateInstalling;

        public UpdateManager? UpdateManager { get; set; }
        public UpdateInfo? UpdateInfo { get; set; }

        [RelayCommand]
        private void CloseUpdateDialog() => IsUpdateDialogVisible = false;

        [RelayCommand]
        private async Task InstallUpdate()
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

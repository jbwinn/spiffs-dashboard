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
            Sync.Start();

            // Run initial database replication check to synchronize DuckDB from LiteDB operational cache
            Sync.QueueRebuild();

            // Wire domain dependencies
            PayoutService = new PayoutLedgerService(LiteDb, Sync);
            Analytics = new AnalyticsService(DuckDb);
            ReportGen = new ReportGenerator();
            CommissionProc = new CommissionProcessor();

            // Setup Workspaces
            DashboardViewModel = new LedgerDashboardViewModel(this);
            SettingsViewModel = new FullScreenSettingsViewModel(this);

            _currentActiveWorkspace = DashboardViewModel;

            // Run update check in background task
            Task.Run(CheckForApplicationUpdatesAsync);
        }

        [RelayCommand]
        public void ToggleWorkspace()
        {
            if (CurrentActiveWorkspace == DashboardViewModel)
            {
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
            Sync.Stop();
            LiteDb.Dispose();
            DuckDb.Dispose();
        }

        private async Task CheckForApplicationUpdatesAsync()
        {
            try
            {
                // Retrieve the update source dynamically targeting GitHub Releases.
                // Replace the repository URL with the production repository deployment target.
                var updateSource = new GithubSource("https://github.com/jbwinn/spiffs-dashboard", null, false);
                var updateManager = new UpdateManager(updateSource);
                var updateInfo = await updateManager.CheckForUpdatesAsync();
                
                if (updateInfo != null)
                {
                    // Download update differences in the background without locking the UI thread
                    await updateManager.DownloadUpdatesAsync(updateInfo);
                    
                    // Apply updates smoothly during the next application launch lifecycle
                    updateManager.ApplyUpdatesAndExit(updateInfo);
                }
            }
            catch (Exception)
            {
                // Fail silently to safeguard application operations if the host machine goes offline
            }
        }
    }
}

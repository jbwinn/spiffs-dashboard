using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Threading;
using SalesLedger.Core.ViewModels;

namespace SalesLedger.Tests.UiTests
{
    /// <summary>Builds the real app (theme, resources, views) on Avalonia's headless platform.</summary>
    public static class HeadlessTestApp
    {
        public static AppBuilder BuildAvaloniaApp()
        {
            App.EnableDeveloperTools = false;
            return AppBuilder.Configure<App>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
        }
    }

    /// <summary>
    /// Owns the headless Avalonia session (one UI thread per process) and shuts it down after the UI tests,
    /// otherwise its thread keeps the test process alive.
    /// </summary>
    public sealed class HeadlessSessionFixture : IDisposable
    {
        public HeadlessUnitTestSession Session { get; } = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));

        public void Dispose() => Session.Dispose();
    }

    [CollectionDefinition(Name)]
    public sealed class HeadlessUiCollection : ICollectionFixture<HeadlessSessionFixture>
    {
        public const string Name = "Headless UI";
    }

    /// <summary>
    /// Runs a UI test on the session's UI thread with a throwaway data folder: real databases and the real reports
    /// folder are never touched, and a fake shell means no PDF viewer or printer is ever launched.
    /// </summary>
    public static class HeadlessUi
    {
        public static void Run(HeadlessSessionFixture fixture, Action<MainWindowViewModel> test) =>
            Run(fixture, (vm, _) => test(vm));

        public static void Run(HeadlessSessionFixture fixture, Action<MainWindowViewModel, FakeShellActions> test)
        {
            var dir = Path.Combine(Path.GetTempPath(), $"ledger_ui_{Guid.NewGuid():N}");
            try
            {
                fixture.Session.Dispatch(() =>
                {
                    var shell = new FakeShellActions();
                    var vm = new MainWindowViewModel(dir, checkForUpdates: false, shell);
                    vm.ReportService.SetReportsFolder(Path.Combine(dir, "reports"));
                    try
                    {
                        test(vm, shell);
                    }
                    finally
                    {
                        // Close any window a failed test left open, so later tests start clean
                        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                        {
                            foreach (var w in desktop.Windows.ToList()) w.Close();
                        }
                        vm.Dispose();
                    }
                }, CancellationToken.None).GetAwaiter().GetResult();
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch (Exception) { /* ignore */ }
            }
        }

        /// <summary>Lets bindings, layout and rendering catch up (hit testing uses the rendered scene).</summary>
        public static void Settle()
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }
}

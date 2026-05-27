using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace SalesLedger;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
#if DEBUG
        this.AttachDeveloperTools();
#endif
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainVm = new Core.ViewModels.MainWindowViewModel();
            desktop.MainWindow = new MainWindow
            {
                DataContext = mainVm
            };

            desktop.Exit += (sender, args) =>
            {
                mainVm.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
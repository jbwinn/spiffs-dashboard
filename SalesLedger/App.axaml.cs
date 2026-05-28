using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using SalesLedger.Core.Theme;

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
        // Populate global theme brushes from AppTheme C# color tokens
        Resources["BgAppDark"] = new SolidColorBrush(Color.Parse(AppTheme.BgApp));
        Resources["BgCardDark"] = new SolidColorBrush(Color.Parse(AppTheme.BgCard));
        Resources["BgOverlay"] = new SolidColorBrush(Color.Parse(AppTheme.BgOverlay));
        Resources["BorderSlate"] = new SolidColorBrush(Color.Parse(AppTheme.Border));
        Resources["TextPrimary"] = new SolidColorBrush(Color.Parse(AppTheme.TextPrimary));
        Resources["TextSecondary"] = new SolidColorBrush(Color.Parse(AppTheme.TextSecondary));
        Resources["TextMuted"] = new SolidColorBrush(Color.Parse(AppTheme.TextMuted));
        Resources["AccentBlue"] = new SolidColorBrush(Color.Parse(AppTheme.AccentBlue));
        Resources["AccentBlueHover"] = new SolidColorBrush(Color.Parse(AppTheme.AccentBlueHover));
        Resources["AccentGreen"] = new SolidColorBrush(Color.Parse(AppTheme.AccentGreen));
        Resources["AccentGreenHover"] = new SolidColorBrush(Color.Parse(AppTheme.AccentGreenHover));
        Resources["AccentRed"] = new SolidColorBrush(Color.Parse(AppTheme.AccentRed));
        Resources["AccentRedHover"] = new SolidColorBrush(Color.Parse(AppTheme.AccentRedHover));
        Resources["AccentPurple"] = new SolidColorBrush(Color.Parse(AppTheme.AccentPurple));
        Resources["AccentPurpleHover"] = new SolidColorBrush(Color.Parse(AppTheme.AccentPurpleHover));
        Resources["AccentOrange"] = new SolidColorBrush(Color.Parse(AppTheme.AccentYellow));
        Resources["AccentPink"] = new SolidColorBrush(Color.Parse(AppTheme.AccentBrown));
        Resources["AccentPinkHover"] = new SolidColorBrush(Color.Parse(AppTheme.AccentBrownHover));

        // Register Brand Typography Families
        Resources["FontPrimary"] = new FontFamily(AppTheme.FontPrimaryName);
        Resources["FontSecondary"] = new FontFamily(AppTheme.FontSecondaryName);

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
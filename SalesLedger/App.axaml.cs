using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SalesLedger.Core.Theme;

namespace SalesLedger;

public class App : Application
{
    /// <summary>Debug builds attach Avalonia's developer tools; UI tests turn this off (it keeps the test process alive).</summary>
    public static bool EnableDeveloperTools { get; set; } = true;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
#if DEBUG
        if (EnableDeveloperTools)
        {
            this.AttachDeveloperTools();
        }
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
        Resources["AccentYellow"] = new SolidColorBrush(Color.Parse(AppTheme.AccentYellow));
        Resources["AccentPink"] = new SolidColorBrush(Color.Parse(AppTheme.AccentBrown));
        Resources["AccentPinkHover"] = new SolidColorBrush(Color.Parse(AppTheme.AccentBrownHover));

        // Header navigation colors (one accent per page)
        Resources["NavDashboard"] = new SolidColorBrush(Color.Parse(AppTheme.NavDashboard));
        Resources["NavDashboardHover"] = new SolidColorBrush(Color.Parse(AppTheme.NavDashboardHover));
        Resources["NavDashboardText"] = new SolidColorBrush(Color.Parse(AppTheme.NavDashboardText));
        Resources["NavReports"] = new SolidColorBrush(Color.Parse(AppTheme.NavReports));
        Resources["NavReportsHover"] = new SolidColorBrush(Color.Parse(AppTheme.NavReportsHover));
        Resources["NavReportsText"] = new SolidColorBrush(Color.Parse(AppTheme.NavReportsText));
        Resources["NavSettings"] = new SolidColorBrush(Color.Parse(AppTheme.NavSettings));
        Resources["NavSettingsHover"] = new SolidColorBrush(Color.Parse(AppTheme.NavSettingsHover));
        Resources["NavSettingsText"] = new SolidColorBrush(Color.Parse(AppTheme.NavSettingsText));

        // Register Brand Typography Families
        Resources["FontPrimary"] = new FontFamily(AppTheme.FontPrimaryName);
        Resources["FontSecondary"] = new FontFamily(AppTheme.FontSecondaryName);

        // Number boxes (prices, refunds, rule values) select their contents when focused, so typing "250"
        // replaces "$0.00" instead of producing "$0.00250"
        InputElement.GotFocusEvent.AddClassHandler<TextBox>((textBox, _) =>
        {
            if (textBox.FindAncestorOfType<NumericUpDown>() != null)
            {
                Dispatcher.UIThread.Post(textBox.SelectAll, DispatcherPriority.Input);
            }
        }, handledEventsToo: true);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainVm = new Core.ViewModels.MainWindowViewModel();
            desktop.MainWindow = new MainWindow
            {
                DataContext = mainVm
            };

            desktop.Exit += (_, _) =>
            {
                mainVm.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SalesLedger.Core.Services;

namespace SalesLedger.Core.ViewModels
{
    /// <summary>
    /// The Payout Reports page: the open pay period (preview / finalize) and the history of finalized reports
    /// (view, print, copy, show in folder, regenerate).
    /// </summary>
    public partial class ReportsViewModel : ObservableObject
    {
        private readonly MainWindowViewModel _mainVm;

        public ReportsViewModel(MainWindowViewModel mainVm)
        {
            _mainVm = mainVm ?? throw new ArgumentNullException(nameof(mainVm));
        }

        private ReportService Reports => _mainVm.ReportService;
        private IShellActions Shell => _mainVm.Shell;

        // ------------------------------------------------------------------
        // Current (open) pay period
        // ------------------------------------------------------------------

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ShowFinalizeDialogCommand))]
        [NotifyCanExecuteChangedFor(nameof(PreviewCurrentPeriodCommand))]
        public partial bool HasOpenItems { get; private set; }

        [ObservableProperty] public partial string OpenItemsText { get; private set; } = string.Empty;
        [ObservableProperty] public partial string OpenGrossText { get; private set; } = string.Empty;
        [ObservableProperty] public partial string OpenReturnsText { get; private set; } = string.Empty;
        [ObservableProperty] public partial string OpenNetText { get; private set; } = string.Empty;
        [ObservableProperty] public partial string OpenPayoutText { get; private set; } = string.Empty;
        [ObservableProperty] public partial string OpenPeriodDescription { get; private set; } = string.Empty;

        // ------------------------------------------------------------------
        // History
        // ------------------------------------------------------------------

        public ObservableCollection<ReportRowViewModel> History { get; } = [];

        [ObservableProperty] public partial bool HasHistory { get; private set; }
        [ObservableProperty] public partial string ReportsFolderText { get; private set; } = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
        public partial string StatusMessage { get; private set; } = string.Empty;

        [ObservableProperty] public partial bool IsStatusError { get; private set; }
        public bool HasStatusMessage => !string.IsNullOrEmpty(StatusMessage);

        // ------------------------------------------------------------------
        // Finalize dialog
        // ------------------------------------------------------------------

        [ObservableProperty] public partial bool IsFinalizeVisible { get; private set; }
        [ObservableProperty] public partial string PeriodReportName { get; set; } = string.Empty;
        [ObservableProperty] public partial string FinalizeSummaryText { get; private set; } = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasFinalizeError))]
        public partial string FinalizeError { get; private set; } = string.Empty;

        public bool HasFinalizeError => !string.IsNullOrEmpty(FinalizeError);

        // ------------------------------------------------------------------

        public void Refresh()
        {
            var open = Reports.GetOpenPeriodSummary();
            HasOpenItems = open.ItemCount > 0;
            OpenItemsText = open.ItemCount.ToString();
            OpenGrossText = open.Totals.GrossSales.ToString("C");
            OpenReturnsText = (-open.Totals.Returns).ToString("C");
            OpenNetText = open.Totals.NetSales.ToString("C");
            OpenPayoutText = open.Totals.Commission.ToString("C");

            var since = open.LastReport is { } last
                ? $"Open since the last finalize on {last.ReportGeneratedTimestamp.ToLocalTime():MMM d, yyyy} (\"{last.ReportName}\")."
                : "No pay period has been finalized yet.";
            var dates = open.FirstSaleDate is { } first && open.LastSaleDate is { } lastSale
                ? $" Includes sales dated {first:MMM d, yyyy} – {lastSale:MMM d, yyyy}."
                : string.Empty;
            OpenPeriodDescription = HasOpenItems
                ? since + dates
                : since + " Nothing has been entered since then.";

            History.Clear();
            foreach (var entry in Reports.GetHistory())
            {
                History.Add(new ReportRowViewModel(entry));
            }
            HasHistory = History.Count > 0;
            ReportsFolderText = Reports.ReportsFolder;
        }

        private void ShowStatus(string message, bool isError = false)
        {
            StatusMessage = message;
            IsStatusError = isError;
        }

        [RelayCommand]
        private void DismissStatus() => StatusMessage = string.Empty;

        [RelayCommand(CanExecute = nameof(HasOpenItems))]
        private void PreviewCurrentPeriod()
        {
            try
            {
                var path = Reports.GeneratePreview();
                Shell.Open(path);
                ShowStatus("Preview opened. Nothing has been finalized or saved to your reports folder.");
            }
            catch (Exception ex)
            {
                ShowStatus($"Couldn't create the preview: {ex.Message}", isError: true);
            }
        }

        [RelayCommand(CanExecute = nameof(HasOpenItems))]
        private void ShowFinalizeDialog()
        {
            var open = Reports.GetOpenPeriodSummary();
            PeriodReportName = $"{DateTime.Now:MMMM yyyy} Submission";
            FinalizeError = string.Empty;
            FinalizeSummaryText =
                $"{open.ItemCount} item(s) · Gross {open.Totals.GrossSales:C} · Returns {-open.Totals.Returns:C} · " +
                $"Net {open.Totals.NetSales:C} · Commission payout {open.Totals.Commission:C}";
            IsFinalizeVisible = true;
        }

        [RelayCommand]
        private void CloseFinalizeDialog() => IsFinalizeVisible = false;

        [RelayCommand]
        private void ExecuteFinalize()
        {
            FinalizeError = string.Empty;
            string path;
            try
            {
                (_, path) = Reports.FinalizePeriod(PeriodReportName);
            }
            catch (Exception ex)
            {
                FinalizeError = ex.Message;
                return;
            }

            IsFinalizeVisible = false;
            Refresh();
            ShowStatus($"Pay period finalized. The report was saved as \"{Path.GetFileName(path)}\" and opened for printing.");
            TryOpen(path);
            _mainVm.NotifyLedgerChanged();
        }

        // ------------------------------------------------------------------
        // Report actions
        // ------------------------------------------------------------------

        [RelayCommand]
        private void ViewReport(ReportRowViewModel? row)
        {
            if (EnsurePdf(row) is { } path)
            {
                TryOpen(path);
            }
        }

        [RelayCommand]
        private void PrintReport(ReportRowViewModel? row)
        {
            if (EnsurePdf(row) is not { } path) return;

            if (Shell.Print(path))
            {
                ShowStatus($"\"{row!.Name}\" was sent to your PDF app for printing.");
            }
            else
            {
                // The default PDF app (e.g. a browser) doesn't take print commands: open it instead
                TryOpen(path);
                ShowStatus($"Your PDF app can't print directly, so \"{row!.Name}\" was opened instead. Print it from there (Ctrl+P).");
            }
        }

        [RelayCommand]
        private async Task CopyReport(ReportRowViewModel? row)
        {
            if (EnsurePdf(row) is not { } path) return;

            bool copied;
            try
            {
                copied = await Shell.CopyFileToClipboard(path);
            }
            catch (Exception)
            {
                copied = false;
            }

            ShowStatus(copied
                    ? $"\"{row!.Name}\" PDF copied. Paste it into an email or message (Ctrl+V)."
                    : "Couldn't copy the file to the clipboard. Use Show in Folder and attach it from there.",
                isError: !copied);
        }

        [RelayCommand]
        private void ShowReportInFolder(ReportRowViewModel? row)
        {
            if (EnsurePdf(row) is { } path)
            {
                Shell.ShowInFolder(path);
            }
        }

        [RelayCommand]
        private void RegenerateReport(ReportRowViewModel? row)
        {
            if (row == null) return;
            try
            {
                var path = Reports.RegeneratePdf(row.ReportId);
                Refresh();
                ShowStatus($"\"{row.Name}\" was rebuilt from the ledger and saved as \"{Path.GetFileName(path)}\".");
            }
            catch (Exception ex)
            {
                ShowStatus($"Couldn't regenerate the report: {ex.Message} (Is the PDF open in another program?)", isError: true);
            }
        }

        [RelayCommand]
        private void OpenReportsFolder()
        {
            try
            {
                Directory.CreateDirectory(Reports.ReportsFolder);
                Shell.OpenFolder(Reports.ReportsFolder);
            }
            catch (Exception ex)
            {
                ShowStatus($"Couldn't open the folder: {ex.Message}", isError: true);
            }
        }

        /// <summary>The report's PDF, created from the ledger first if it doesn't exist (older reports, moved/deleted files).</summary>
        private string? EnsurePdf(ReportRowViewModel? row)
        {
            if (row == null) return null;
            if (row.PdfExists) return row.PdfPath;

            try
            {
                var path = Reports.RegeneratePdf(row.ReportId);
                Refresh();
                ShowStatus($"The PDF for \"{row.Name}\" wasn't in your reports folder, so it was rebuilt from the ledger.");
                return path;
            }
            catch (Exception ex)
            {
                ShowStatus($"Couldn't create the PDF: {ex.Message}", isError: true);
                return null;
            }
        }

        private void TryOpen(string path)
        {
            try
            {
                Shell.Open(path);
            }
            catch (Exception)
            {
                ShowStatus($"The report was saved, but no PDF app opened it. It's at {path}.", isError: true);
            }
        }
    }

    /// <summary>One finalized pay period in the history list.</summary>
    public sealed class ReportRowViewModel(ReportHistoryEntry entry)
    {
        public Guid ReportId { get; } = entry.Report.Id;
        public string Name { get; } = entry.Report.ReportName;
        public string PdfPath { get; } = entry.Report.PdfPath;
        public bool PdfExists { get; } = entry.PdfExists;

        public string FinalizedText { get; } = $"Finalized {entry.FinalizedAt:MMM d, yyyy 'at' h:mm tt}";

        /// <summary>The pay period as it actually ran: from the previous finalize to this one, and the sale dates it held.</summary>
        public string CoverageText { get; } =
            (entry.PreviousFinalizedAt is { } prev
                ? $"Period: {prev:MMM d, yyyy} – {entry.FinalizedAt:MMM d, yyyy}"
                : $"Period: through {entry.FinalizedAt:MMM d, yyyy} (first report)")
            + (entry.FirstSaleDate is { } first && entry.LastSaleDate is { } last
                ? $" · Sales dated {first:MMM d} – {last:MMM d, yyyy}"
                : string.Empty);

        public string ItemsText { get; } = $"{entry.ItemCount} item(s)";
        public string GrossText { get; } = entry.Totals.GrossSales.ToString("C");
        public string NetText { get; } = entry.Totals.NetSales.ToString("C");
        public string PayoutText { get; } = entry.Totals.Commission.ToString("C");

        public string FileStatusText { get; } = entry.PdfExists
            ? Path.GetFileName(entry.Report.PdfPath)
            : string.IsNullOrEmpty(entry.Report.PdfPath)
                ? "No PDF saved yet. It will be created from the ledger when you view, print or copy it."
                : "PDF not found (moved or deleted). It will be rebuilt from the ledger when you view, print or copy it.";

        public bool IsFileMissing => !PdfExists;
    }
}

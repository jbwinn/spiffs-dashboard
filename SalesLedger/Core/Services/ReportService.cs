using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SalesLedger.Core.Models;

namespace SalesLedger.Core.Services
{
    /// <summary>A finalized pay period, with what it covered.</summary>
    public sealed class ReportHistoryEntry
    {
        public required PayoutReport Report { get; init; }
        public required PeriodTotals Totals { get; init; }
        public int ItemCount { get; init; }

        /// <summary>When it was finalized (local time).</summary>
        public DateTime FinalizedAt => Report.ReportGeneratedTimestamp.ToLocalTime();

        /// <summary>When the previous pay period was finalized, i.e. when this one opened. Null for the first report.</summary>
        public DateTime? PreviousFinalizedAt { get; init; }

        public DateTime? FirstSaleDate { get; init; }
        public DateTime? LastSaleDate { get; init; }
        public bool PdfExists => !string.IsNullOrEmpty(Report.PdfPath) && File.Exists(Report.PdfPath);
    }

    /// <summary>What the next Finalize would include.</summary>
    public sealed class OpenPeriodSummary
    {
        public required PeriodTotals Totals { get; init; }
        public int ItemCount { get; init; }
        public DateTime? FirstSaleDate { get; init; }
        public DateTime? LastSaleDate { get; init; }
        public PayoutReport? LastReport { get; init; }
    }

    /// <summary>
    /// Payout reports: finalizing a period, saving its PDF to the reports folder, regenerating, previews and history.
    /// Paid records never change, so any finalized report can be regenerated exactly from the ledger.
    /// </summary>
    public class ReportService(LiteDbService db, PayoutLedgerService payout, ReportGenerator generator)
    {
#if DEBUG
        // Development builds use a separate folder so test reports never mix with real ones
        private const string DefaultFolderName = "Sales Ledger Reports (Dev)";
#else
        private const string DefaultFolderName = "Sales Ledger Reports";
#endif

        public static string DefaultReportsFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), DefaultFolderName);

        /// <summary>The folder report PDFs are saved to (the user's choice, or the default).</summary>
        public string ReportsFolder
        {
            get
            {
                var custom = db.GetUserSettings().ReportsFolder;
                return string.IsNullOrWhiteSpace(custom) ? DefaultReportsFolder : custom;
            }
        }

        public bool IsUsingDefaultFolder => string.IsNullOrWhiteSpace(db.GetUserSettings().ReportsFolder);

        /// <summary>Sets the reports folder; null or blank goes back to the default.</summary>
        public void SetReportsFolder(string? folder)
        {
            var settings = db.GetUserSettings();
            settings.ReportsFolder = folder?.Trim() ?? string.Empty;
            db.SaveUserSettings(settings);
        }

        /// <summary>Records a finalize would include: everything pending, plus returns before payout not yet on a report.</summary>
        public List<SaleRecord> GetOpenPeriodSales() =>
            db.Sales.Find(x => x.Status == PayoutStatus.Pending ||
                               (x.Status == PayoutStatus.ReturnedBeforePayout && x.AssociatedReportId == null))
                .ToList();

        public List<SaleRecord> GetReportSales(Guid reportId) =>
            db.Sales.Find(x => x.AssociatedReportId == reportId).ToList();

        public OpenPeriodSummary GetOpenPeriodSummary()
        {
            var sales = GetOpenPeriodSales();
            var dates = sales.Where(PeriodTotals.IsSaleRow).Select(s => s.TransactionDate).ToList();
            return new OpenPeriodSummary
            {
                Totals = PeriodTotals.From(sales),
                ItemCount = sales.Count,
                FirstSaleDate = dates.Count > 0 ? dates.Min() : null,
                LastSaleDate = dates.Count > 0 ? dates.Max() : null,
                LastReport = db.Reports.FindAll().OrderByDescending(r => r.ReportGeneratedTimestamp).FirstOrDefault()
            };
        }

        /// <summary>Every finalized pay period, newest first.</summary>
        public List<ReportHistoryEntry> GetHistory()
        {
            var reports = db.Reports.FindAll().OrderBy(r => r.ReportGeneratedTimestamp).ToList();
            var entries = new List<ReportHistoryEntry>();

            for (int i = 0; i < reports.Count; i++)
            {
                var report = reports[i];
                var sales = GetReportSales(report.Id);
                var dates = sales.Where(PeriodTotals.IsSaleRow).Select(s => s.TransactionDate).ToList();

                entries.Add(new ReportHistoryEntry
                {
                    Report = report,
                    Totals = PeriodTotals.From(sales),
                    ItemCount = sales.Count,
                    PreviousFinalizedAt = i > 0 ? reports[i - 1].ReportGeneratedTimestamp.ToLocalTime() : null,
                    FirstSaleDate = dates.Count > 0 ? dates.Min() : null,
                    LastSaleDate = dates.Count > 0 ? dates.Max() : null
                });
            }

            entries.Reverse();
            return entries;
        }

        /// <summary>
        /// Finalizes the open pay period (locks everything as Paid) and saves its PDF to the reports folder.
        /// Throws InvalidOperationException if there's nothing to finalize.
        /// </summary>
        public (PayoutReport Report, string PdfPath) FinalizePeriod(string reportName)
        {
            if (string.IsNullOrWhiteSpace(reportName))
            {
                throw new InvalidOperationException("Enter a name for this pay period's report.");
            }

            if (GetOpenPeriodSales().Count == 0)
            {
                throw new InvalidOperationException("There's nothing to finalize: the current pay period has no sales or returns.");
            }

            var report = payout.CloseCurrentPayPeriod(reportName.Trim());
            var path = WritePdf(report, reprintedAt: null);
            return (report, path);
        }

        /// <summary>
        /// Rebuilds a finalized report's PDF from the ledger into the reports folder (replacing a previous copy there)
        /// and records the new location. Returns the path.
        /// </summary>
        public string RegeneratePdf(Guid reportId)
        {
            var report = db.Reports.FindById(reportId) ?? throw new InvalidOperationException("Report not found.");
            return WritePdf(report, reprintedAt: DateTime.Now);
        }

        /// <summary>A preview of the open period, saved to a temporary folder (not the reports folder). Returns the path.</summary>
        public string GeneratePreview()
        {
            var sales = GetOpenPeriodSales();
            var tempReport = new PayoutReport
            {
                Id = Guid.Empty,
                ReportGeneratedTimestamp = DateTime.UtcNow,
                ReportName = "Preview (Open Period)",
                TotalCommissionCalculated = sales.Where(s => s.Status == PayoutStatus.Pending).Sum(s => s.CalculatedCommission),
                LockedSaleIds = sales.Select(s => s.Id).ToList()
            };

            var folder = Path.Combine(Path.GetTempPath(), "Sales Ledger Previews");
            var path = Path.Combine(folder, $"Preview {DateTime.Now:yyyy-MM-dd HHmmss}.pdf");
            generator.GeneratePdfReport(tempReport, sales, db.GetUserSettings(), path, isPreview: true);
            return path;
        }

        /// <summary>"2026-11-03 November 2026 Submission.pdf", with characters Windows doesn't allow removed.</summary>
        public static string BuildFileName(PayoutReport report)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var name = new string(report.ReportName.Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim().TrimEnd('.');
            if (name.Length == 0) name = "Payout Report";
            return $"{report.ReportGeneratedTimestamp.ToLocalTime():yyyy-MM-dd} {name}.pdf";
        }

        private string WritePdf(PayoutReport report, DateTime? reprintedAt)
        {
            var folder = Path.GetFullPath(ReportsFolder);
            Directory.CreateDirectory(folder);

            string path;
            var previous = SafeFullPath(report.PdfPath);
            if (previous.Length > 0 && string.Equals(Path.GetDirectoryName(previous), folder.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                // Regenerating into the same folder replaces this report's own file
                path = previous;
            }
            else
            {
                // Two different reports with the same date and name get distinct files
                path = Path.Combine(folder, BuildFileName(report));
                var stem = Path.GetFileNameWithoutExtension(path);
                for (int n = 2; File.Exists(path); n++)
                {
                    path = Path.Combine(folder, $"{stem} ({n}).pdf");
                }
            }

            generator.GeneratePdfReport(report, GetReportSales(report.Id), db.GetUserSettings(), path, isPreview: false, reprintedAt);

            report.PdfPath = path;
            db.Reports.Update(report);
            return path;
        }

        private static string SafeFullPath(string path)
        {
            try { return string.IsNullOrEmpty(path) ? string.Empty : Path.GetFullPath(path); }
            catch (Exception) { return string.Empty; }
        }
    }
}

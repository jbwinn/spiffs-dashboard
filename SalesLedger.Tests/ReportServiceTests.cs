using SalesLedger.Core.Models;
using SalesLedger.Core.Services;

namespace SalesLedger.Tests
{
    /// <summary>Report storage, finalize, regenerate, preview and history (pay periods don't follow calendar months).</summary>
    public class ReportServiceTests : LedgerTestBase
    {
        [Fact]
        public void ReportsFolder_DefaultsToDocuments_AndCanBeChanged()
        {
            Reports.SetReportsFolder(null);
            Assert.True(Reports.IsUsingDefaultFolder);
            Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Reports.ReportsFolder);
            Assert.Contains("Sales Ledger Reports", Reports.ReportsFolder);

            Reports.SetReportsFolder(ReportsFolder);
            Assert.False(Reports.IsUsingDefaultFolder);
            Assert.Equal(ReportsFolder, Reports.ReportsFolder);
        }

        [Fact]
        public void Finalize_LocksThePeriod_SavesThePdfToTheReportsFolder_AndRemembersWhere()
        {
            AddSale(NewLens(1000m, "INV-1"));
            AddSale(NewLens(500m, "INV-2"));

            var (report, path) = Reports.FinalizePeriod("November 2026 Submission");

            Assert.True(File.Exists(path));
            Assert.Equal(ReportsFolder, Path.GetDirectoryName(path));
            Assert.Equal($"{DateTime.Now:yyyy-MM-dd} November 2026 Submission.pdf", Path.GetFileName(path));
            Assert.Equal(path, Lite.Reports.FindById(report.Id).PdfPath);
            Assert.All(Lite.Sales.FindAll(), s => Assert.Equal(PayoutStatus.Paid, s.Status));
            Assert.Empty(Reports.GetOpenPeriodSales());
        }

        [Fact]
        public void Finalize_WithNothingOpen_IsRejected()
        {
            var ex = Assert.Throws<InvalidOperationException>(() => Reports.FinalizePeriod("Empty"));
            Assert.Contains("nothing to finalize", ex.Message);
            Assert.Empty(Lite.Reports.FindAll());
        }

        [Fact]
        public void History_ShowsEachPeriodFromTheLastFinalizeToItsOwn_NewestFirst()
        {
            AddSale(NewLens(1000m, "INV-OCT", new DateTime(2026, 10, 2)));
            var (first, _) = Reports.FinalizePeriod("First");
            AddSale(NewLens(500m, "INV-NOV1", new DateTime(2026, 10, 30)));
            var returned = AddSale(NewLens(200m, "INV-NOV2", new DateTime(2026, 11, 1)));
            Payout.ProcessReturn(returned.Id);
            var (second, _) = Reports.FinalizePeriod("Second");

            var history = Reports.GetHistory();

            Assert.Equal([second.Id, first.Id], history.Select(h => h.Report.Id));
            Assert.Null(history[1].PreviousFinalizedAt);
            Assert.Equal(Lite.Reports.FindById(first.Id).ReportGeneratedTimestamp.ToLocalTime(), history[0].PreviousFinalizedAt);
            Assert.Equal(new DateTime(2026, 10, 30), history[0].FirstSaleDate);
            Assert.Equal(new DateTime(2026, 11, 1), history[0].LastSaleDate);
            Assert.Equal(700m, history[0].Totals.GrossSales);
            Assert.Equal(200m, history[0].Totals.Returns);
            Assert.Equal(second.TotalCommissionCalculated, history[0].Totals.Commission);
            Assert.All(history, h => Assert.True(h.PdfExists));
        }

        [Fact]
        public void Regenerate_RebuildsAMissingPdf_InTheSameFile()
        {
            AddSale(NewLens(1000m));
            var (report, path) = Reports.FinalizePeriod("Lost Report");
            File.Delete(path);
            Assert.False(Reports.GetHistory().Single().PdfExists);

            var rebuilt = Reports.RegeneratePdf(report.Id);

            Assert.Equal(path, rebuilt);
            Assert.True(File.Exists(rebuilt));
            Assert.True(Reports.GetHistory().Single().PdfExists);
        }

        [Fact]
        public void Regenerate_ForAnOlderReportWithNoSavedPdf_SavesItToTheReportsFolder()
        {
            // Reports finalized before reports were stored have no PdfPath
            AddSale(NewLens(1000m));
            var report = Payout.CloseCurrentPayPeriod("Before This Feature");
            Assert.Equal(string.Empty, Lite.Reports.FindById(report.Id).PdfPath);

            var path = Reports.RegeneratePdf(report.Id);

            Assert.Equal(ReportsFolder, Path.GetDirectoryName(path));
            Assert.True(File.Exists(path));
        }

        [Fact]
        public void TwoReportsWithTheSameNameAndDay_GetSeparateFiles()
        {
            AddSale(NewLens(1000m, "INV-1"));
            var (_, first) = Reports.FinalizePeriod("Same Name");
            AddSale(NewLens(500m, "INV-2"));
            var (_, second) = Reports.FinalizePeriod("Same Name");

            Assert.NotEqual(first, second);
            Assert.EndsWith("Same Name (2).pdf", second);
            Assert.True(File.Exists(first) && File.Exists(second));
        }

        [Fact]
        public void FileNames_DropCharactersWindowsDoesNotAllow()
        {
            var report = new PayoutReport { ReportName = "Oct/Nov: \"Q4\" Submission?", ReportGeneratedTimestamp = new DateTime(2026, 11, 3, 15, 0, 0, DateTimeKind.Local) };

            var name = ReportService.BuildFileName(report);

            Assert.Equal("2026-11-03 Oct-Nov- -Q4- Submission-.pdf", name);
            Assert.Equal(-1, name.IndexOfAny(Path.GetInvalidFileNameChars()));
        }

        [Fact]
        public void Preview_DoesNotLockAnything_OrSaveToTheReportsFolder()
        {
            AddSale(NewLens(1000m));

            var path = Reports.GeneratePreview();

            try
            {
                Assert.True(File.Exists(path));
                Assert.NotEqual(ReportsFolder, Path.GetDirectoryName(path));
                Assert.Single(Reports.GetOpenPeriodSales());
                Assert.Empty(Lite.Reports.FindAll());
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void OpenPeriodSummary_MatchesWhatFinalizeWillLock()
        {
            AddSale(NewLens(1000m, "INV-1", new DateTime(2026, 9, 15)));   // unpaid from an older month still counts
            var early = AddSale(NewLens(400m, "INV-2", new DateTime(2026, 11, 2)));
            Payout.ProcessReturn(early.Id);

            var summary = Reports.GetOpenPeriodSummary();
            var (report, _) = Reports.FinalizePeriod("Check");

            Assert.Equal(2, summary.ItemCount);
            Assert.Equal(new DateTime(2026, 9, 15), summary.FirstSaleDate);
            Assert.Equal(report.TotalCommissionCalculated, summary.Totals.Commission);
            Assert.Equal(summary.ItemCount, report.LockedSaleIds.Count);
        }
    }
}

using Avalonia.Controls;
using Avalonia.VisualTree;
using SalesLedger.Core.Models;
using SalesLedger.Core.ViewModels;

namespace SalesLedger.Tests.UiTests
{
    /// <summary>The Payout Reports page, driven headlessly. A fake shell records open/print/copy instead of launching anything.</summary>
    [Collection(HeadlessUiCollection.Name)]
    public class ReportsPageUiTests(HeadlessSessionFixture session)
    {
        private static void Seed(MainWindowViewModel vm, string invoice, decimal price)
        {
            var sale = new StandardSale { InvoiceNumber = invoice, ProductName = "Lens " + invoice, Category = "Lens", SalePrice = price, TransactionDate = DateTime.Today.AddDays(-2) };
            vm.CommissionProc.ApplyCommission(sale, vm.LiteDb.GetUserSettings().ActiveRules ?? []);
            vm.LiteDb.Sales.Insert(sale);
        }

        private static ReportsViewModel GoToReports(MainWindowViewModel vm, out Window window)
        {
            window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();
            HeadlessUi.Settle();

            // Click the header's "Payout Reports" tab
            var tab = window.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "Payout Reports");
            tab.Command!.Execute(tab.CommandParameter);
            HeadlessUi.Settle();

            Assert.True(vm.IsReportsActive);
            return (ReportsViewModel)vm.CurrentActiveWorkspace;
        }

        [Fact]
        public void FinalizeFromTheReportsPage_SavesAndOpensThePdf_AndListsItInHistory()
        {
            HeadlessUi.Run(session, (vm, shell) =>
            {
                Seed(vm, "INV-1", 1000m);
                var reports = GoToReports(vm, out _);

                Assert.True(reports.HasOpenItems);
                Assert.Equal("$80.00", reports.OpenPayoutText);
                Assert.False(reports.HasHistory);

                reports.ShowFinalizeDialogCommand.Execute(null);
                Assert.True(reports.IsFinalizeVisible);
                reports.PeriodReportName = "Test Period";
                reports.ExecuteFinalizeCommand.Execute(null);
                HeadlessUi.Settle();

                Assert.False(reports.IsFinalizeVisible);
                var row = Assert.Single(reports.History);
                Assert.Equal("Test Period", row.Name);
                Assert.True(row.PdfExists);
                Assert.Equal([row.PdfPath], shell.Opened);   // opened for printing
                Assert.False(reports.HasOpenItems);           // nothing left open
                Assert.Contains("Finalized", row.FinalizedText);
            });
        }

        [Fact]
        public void HistoryActions_Print_Copy_ShowInFolder_AndRegenerate()
        {
            HeadlessUi.Run(session, (vm, shell) =>
            {
                Seed(vm, "INV-1", 1000m);
                vm.ReportService.FinalizePeriod("Actions Period");
                var reports = GoToReports(vm, out _);
                var row = reports.History.Single();

                reports.PrintReportCommand.Execute(row);
                Assert.Equal([row.PdfPath], shell.Printed);

                reports.CopyReportCommand.Execute(row);
                Assert.Equal([row.PdfPath], shell.Copied);

                reports.ShowReportInFolderCommand.Execute(row);
                Assert.Equal([row.PdfPath], shell.ShownInFolder);

                File.Delete(row.PdfPath);
                reports.RegenerateReportCommand.Execute(row);
                Assert.True(File.Exists(row.PdfPath));
                Assert.Contains("rebuilt", reports.StatusMessage);
            });
        }

        [Fact]
        public void Print_WhenThePdfAppCannotPrint_OpensItInstead()
        {
            HeadlessUi.Run(session, (vm, shell) =>
            {
                Seed(vm, "INV-1", 1000m);
                vm.ReportService.FinalizePeriod("Browser Viewer");
                shell.PrintSupported = false;
                var reports = GoToReports(vm, out _);
                var row = reports.History.Single();

                reports.PrintReportCommand.Execute(row);

                Assert.Equal([row.PdfPath], shell.Opened);
                Assert.Contains("Ctrl+P", reports.StatusMessage);
            });
        }

        [Fact]
        public void ViewingAnOlderReportWithNoPdf_RebuildsItFromTheLedger()
        {
            HeadlessUi.Run(session, (vm, shell) =>
            {
                Seed(vm, "INV-1", 1000m);
                vm.PayoutService.CloseCurrentPayPeriod("Finalized Before Reports Were Stored");
                var reports = GoToReports(vm, out _);
                var row = reports.History.Single();
                Assert.False(row.PdfExists);

                reports.ViewReportCommand.Execute(row);

                var opened = Assert.Single(shell.Opened);
                Assert.True(File.Exists(opened));
                Assert.True(reports.History.Single().PdfExists);
            });
        }
    }
}

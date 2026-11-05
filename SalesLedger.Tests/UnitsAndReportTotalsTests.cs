using SalesLedger.Core.Models;
using SalesLedger.Core.Services;

namespace SalesLedger.Tests
{
    /// <summary>Units sold are net of full returns; the PDF's totals match the dashboard's.</summary>
    public class UnitsAndReportTotalsTests : LedgerTestBase
    {
        private readonly AnalyticsService _analytics;

        public UnitsAndReportTotalsTests()
        {
            _analytics = new AnalyticsService(Duck);
        }

        private void MirrorAll()
        {
            Duck.ClearAll();
            foreach (var sale in Lite.Sales.FindAll()) Duck.UpsertSale(sale);
        }

        /// <summary>
        /// Four lenses: one kept, one returned before payout, one paid then fully returned in two partial refunds,
        /// one paid then partially refunded (customer kept it).
        /// </summary>
        private void SeedMixedReturns()
        {
            AddSale(NewLens(1000m, "INV-KEPT"));

            var returnedEarly = AddSale(NewLens(500m, "INV-RBP"));
            Payout.ProcessReturn(returnedEarly.Id);

            var twoStep = AddSale(NewLens(800m, "INV-TWO"), PayoutStatus.Paid);
            Payout.ProcessReturn(twoStep, 300m, DateTime.Now.AddMinutes(-5));
            Payout.ProcessReturn(twoStep, 500m, DateTime.Now);

            var partial = AddSale(NewLens(600m, "INV-PART"), PayoutStatus.Paid);
            Payout.ProcessReturn(partial, 100m);
        }

        [Fact]
        public void UnitsSold_AreNetOfFullReturns_ButNotPartialRefunds()
        {
            SeedMixedReturns();
            MirrorAll();

            var summary = _analytics.GetSummary("Last 30 Days");

            Assert.Equal(4, summary.GrossUnitsSold);
            Assert.Equal(2, summary.UnitsReturned);   // returned-before-payout + the completed two-step return
            Assert.Equal(2, summary.TotalUnitsSold);  // the kept lens and the partially refunded one
        }

        [Fact]
        public void OnlyTheReturnLineThatCompletesTheRefund_IsMarked()
        {
            var sale = AddSale(NewLens(800m), PayoutStatus.Paid);
            Payout.ProcessReturn(sale, 300m, DateTime.Now.AddMinutes(-5));
            Assert.False(Assert.Single(ReturnLinesFor(sale)).CompletesRefund);

            Payout.ProcessReturn(sale, 500m, DateTime.Now);
            var lines = ReturnLinesFor(sale);
            Assert.False(lines[0].CompletesRefund);
            Assert.True(lines[1].CompletesRefund);

            // Lowering the second refund means the item hasn't fully come back after all
            Payout.UpdateReturnLine(lines[1].Id, 400m);
            Assert.False(Reload(lines[1]).CompletesRefund);
        }

        [Fact]
        public void RefreshReturnCompletionFlags_FixesUpReturnsRecordedBeforeTheFlagExisted()
        {
            var sale = AddSale(NewLens(800m), PayoutStatus.Paid);
            Payout.ProcessReturn(sale.Id); // full return
            var line = Assert.Single(ReturnLinesFor(sale));
            line.CompletesRefund = false;  // simulate an older record
            Lite.Sales.Update(line);

            Assert.Equal(1, Payout.RefreshReturnCompletionFlags());
            Assert.True(Reload(line).CompletesRefund);
            Assert.Equal(0, Payout.RefreshReturnCompletionFlags()); // nothing left to fix
        }

        [Fact]
        public void PeriodTotals_MatchTheDashboard_ForTheSameRecords()
        {
            SeedMixedReturns();
            MirrorAll();

            var dashboard = _analytics.GetSummary("Last 30 Days");
            var pdf = PeriodTotals.From(Lite.Sales.FindAll());

            Assert.Equal(dashboard.GrossSales, pdf.GrossSales);
            Assert.Equal(dashboard.Returns, pdf.Returns);
            Assert.Equal(dashboard.NetSales, pdf.NetSales);
            Assert.Equal(dashboard.GrossUnitsSold, pdf.GrossUnits);
            Assert.Equal(dashboard.UnitsReturned, pdf.UnitsReturned);
            Assert.Equal(dashboard.TotalUnitsSold, pdf.NetUnits);
            Assert.Equal(dashboard.TotalCommission, pdf.Commission);
            Assert.Equal(dashboard.AverageSalePrice, pdf.AverageSalePrice);
        }

        [Fact]
        public void PeriodTotals_ForAClosedPeriod_AddUp()
        {
            SeedMixedReturns();
            var report = Payout.CloseCurrentPayPeriod("Test Period");
            var inReport = Lite.Sales.Find(x => x.AssociatedReportId == report.Id).ToList();

            var totals = PeriodTotals.From(inReport);

            // In the period: the kept lens, the returned-before-payout lens, and the three return lines
            Assert.Equal(1500m, totals.GrossSales);
            Assert.Equal(500m + 300m + 500m + 100m, totals.Returns);
            Assert.Equal(report.TotalCommissionCalculated, totals.Commission); // the PDF's payout matches the report
        }
    }
}

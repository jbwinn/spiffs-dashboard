using SalesLedger.Core.Models;
using SalesLedger.Core.Services;

namespace SalesLedger.Tests
{
    public class AnalyticsServiceTests : LedgerTestBase
    {
        private readonly AnalyticsService _analytics;

        public AnalyticsServiceTests()
        {
            _analytics = new AnalyticsService(Duck);
        }

        /// <summary>Inserts into LiteDB and mirrors to DuckDB directly (the sync pipeline isn't running in tests).</summary>
        private T Store<T>(T sale) where T : SaleRecord
        {
            Lite.Sales.Insert(sale);
            Duck.UpsertSale(sale);
            return sale;
        }

        private void MirrorAll()
        {
            Duck.ClearAll();
            foreach (var sale in Lite.Sales.FindAll()) Duck.UpsertSale(sale);
        }

        [Fact]
        public void GetSummary_ReportsGrossSales_ReturnsAndNet()
        {
            var now = DateTime.Now;

            Store(new StandardSale { InvoiceNumber = "INV-S1", Category = "Cameras", SalePrice = 1000m, CalculatedCommission = 50.00m, TransactionDate = now.AddDays(-2) });
            Store(new WarrantySale { InvoiceNumber = "INV-W1", Category = "Warranty", SalePrice = 200m, CalculatedCommission = 25.00m, ManufacturerPrice = 50m, TransactionDate = now.AddDays(-1) });
            Store(new StandardSale
            {
                InvoiceNumber = "INV-R1", Category = "Cameras", SalePrice = 1500m, CalculatedCommission = 0.00m,
                TransactionDate = now.AddDays(-3), Status = PayoutStatus.ReturnedBeforePayout
            });

            var summary = _analytics.GetSummary("Last 30 Days");

            Assert.Equal(2700.00m, summary.GrossSales);   // everything sold, including the returned camera
            Assert.Equal(1500.00m, summary.Returns);
            Assert.Equal(1200.00m, summary.NetSales);
            Assert.Equal(3, summary.GrossUnitsSold);
            Assert.Equal(1, summary.UnitsReturned);
            Assert.Equal(2, summary.TotalUnitsSold);  // net: the returned camera came back
            Assert.Equal(1, summary.ReturnCount);
            Assert.Equal(75.00m, summary.TotalCommission);
            Assert.Equal(600.00m, summary.AverageSalePrice); // camera + warranty plan; the returned camera isn't counted
        }

        [Fact]
        public void AverageSalePrice_IncludesEverySaleType_ButOnlyItemsKept()
        {
            var day = DateTime.Now.AddDays(-2);
            AddSale(new StandardSale { Category = "Lens", SalePrice = 1000m, IsUsedGear = false, TransactionDate = day }); // new
            AddSale(new StandardSale { Category = "Lens", SalePrice = 600m, IsUsedGear = true, TransactionDate = day });   // used
            AddSale(new EbaySale { Category = "Lens", SalePrice = 800m, TransactionDate = day });                           // eBay
            AddSale(new WarrantySale { Category = "Warranty", SalePrice = 200m, ManufacturerPrice = 80m, TransactionDate = day }); // warranty
            var refunded = AddSale(new StandardSale { Category = "Lens", SalePrice = 5000m, TransactionDate = day }, PayoutStatus.Paid);
            Payout.ProcessReturn(refunded.Id);                     // fully refunded after payout: not kept
            var partly = AddSale(new StandardSale { Category = "Lens", SalePrice = 400m, TransactionDate = day }, PayoutStatus.Paid);
            Payout.ProcessReturn(partly, 50m);                     // partial refund: customer kept it
            MirrorAll();

            var summary = _analytics.GetSummary("Last 30 Days");

            Assert.Equal((1000m + 600m + 800m + 200m + 400m) / 5, summary.AverageSalePrice);
            Assert.Equal(summary.AverageSalePrice, PeriodTotals.From(Lite.Sales.FindAll()).AverageSalePrice); // PDF agrees
        }

        [Fact]
        public void GetSummary_PostPayoutReturn_CountsAsAReturn_NotAsNegativeSales()
        {
            var now = DateTime.Now;
            var sale = Store(new StandardSale
            {
                InvoiceNumber = "INV-T100", Category = "Cameras", SalePrice = 1000m, CalculatedCommission = 50.00m,
                TransactionDate = now.AddDays(-10), Status = PayoutStatus.Paid
            });
            Store(new StandardSale
            {
                InvoiceNumber = "INV-T100", ProductName = "[RETURN] - Original Camera", Category = "Cameras",
                SalePrice = -1000m, CalculatedCommission = -50.00m, TransactionDate = now.AddDays(-2),
                IsReturn = true, OriginalSaleId = sale.Id, CompletesRefund = true
            });

            var summary = _analytics.GetSummary("Last 30 Days");

            Assert.Equal(1000.00m, summary.GrossSales);
            Assert.Equal(1000.00m, summary.Returns);
            Assert.Equal(0.00m, summary.NetSales);
            Assert.Equal(1, summary.GrossUnitsSold);
            Assert.Equal(1, summary.ReturnCount);
            Assert.Equal(0.00m, summary.TotalCommission); // commission stays net: it's what gets paid
            Assert.Equal(0.00m, summary.AverageSalePrice); // the only sale came back
            Assert.Equal(0, summary.TotalUnitsSold);
        }

        [Fact]
        public void GetSummary_PartialReturn_ReducesNetByTheRefundOnly()
        {
            var sale = AddSale(NewLens(1000m), PayoutStatus.Paid);
            Payout.ProcessReturn(sale, 250m);
            MirrorAll();

            var summary = _analytics.GetSummary("Last 30 Days");

            Assert.Equal(1000m, summary.GrossSales);
            Assert.Equal(250m, summary.Returns);
            Assert.Equal(750m, summary.NetSales);
            Assert.Equal(60.00m, summary.TotalCommission); // 80 - 20 clawback
        }

        [Fact]
        public void GetTrends_ReturnBeforePayout_IsNotCountedTwice()
        {
            // Before the fix: the blue bar dropped the sale AND the red bar showed the return (net -1000 instead of 0)
            var date = DateTime.Now.AddDays(-3);
            Store(new StandardSale { Category = "Lens", SalePrice = 1000m, TransactionDate = date, Status = PayoutStatus.ReturnedBeforePayout });

            var bucket = _analytics.GetTrends("Last 30 Days").Single(b => b.PeriodStart == date.Date);

            Assert.Equal(1000, bucket.TotalRevenue);                  // gross
            Assert.Equal(-1000, bucket.ReturnOffsetMetric.Revenue);   // the return
            Assert.Equal(0, bucket.TotalRevenue + bucket.ReturnOffsetMetric.Revenue);
        }

        [Fact]
        public void GetTrends_PopulatesContiguousBuckets_AndGrossCategoryMetrics()
        {
            var now = DateTime.Now;
            var sale1 = Store(new StandardSale { InvoiceNumber = "INV-T1", Category = "Cameras", SalePrice = 1000m, CalculatedCommission = 50.00m, TransactionDate = now.AddDays(-5) });
            var sale2 = Store(new StandardSale { InvoiceNumber = "INV-T2", Category = "Lenses", SalePrice = 800m, CalculatedCommission = 64.00m, TransactionDate = now.AddDays(-2) });

            var trends = _analytics.GetTrends("Last 30 Days");

            Assert.Equal(31, trends.Count); // one bucket per day, including empty days
            Assert.Equal(1000, trends.Single(x => x.PeriodStart == sale1.TransactionDate.Date).CategoryBreakdown["Cameras"].Revenue);
            Assert.Equal(800, trends.Single(x => x.PeriodStart == sale2.TransactionDate.Date).CategoryBreakdown["Lenses"].Revenue);
        }

        [Fact]
        public void CurrentPayPeriod_IncludesEveryUnpaidRecord_WhateverItsDate_AndNothingPaid()
        {
            var old = AddSale(NewLens(1000m, "INV-OLD", DateTime.Now.AddMonths(-3)));   // unpaid, months ago
            AddSale(NewLens(500m, "INV-NEW", DateTime.Now.AddDays(-1)));                 // unpaid, recent
            AddSale(NewLens(700m, "INV-PAID", DateTime.Now.AddDays(-2)), PayoutStatus.Paid);
            var paidThenReturned = AddSale(NewLens(400m, "INV-PR", DateTime.Now.AddDays(-20)), PayoutStatus.Paid);
            Payout.ProcessReturn(paidThenReturned, 400m);                                  // pending return line
            MirrorAll();

            var range = _analytics.ResolveRange(AnalyticsService.CurrentPayPeriod);
            var summary = _analytics.GetSummary(range);

            Assert.True(range.CurrentPayPeriodOnly);
            Assert.Equal(old.TransactionDate.Date, range.Start);
            Assert.Equal(1500m, summary.GrossSales);      // the two unpaid sales, not the paid ones
            Assert.Equal(400m, summary.Returns);          // the unpaid return line
            Assert.Equal(120.00m - 32.00m, summary.TotalCommission); // 80 + 40 - 32 clawback: what the next payout totals
            Assert.Equal("week", range.Scale);            // ~3 months of data
        }

        [Fact]
        public void CurrentPayPeriod_ExcludesReturnsAlreadyOnAReport()
        {
            var returned = AddSale(NewLens(1000m));
            Payout.ProcessReturn(returned.Id);             // returned before payout
            Payout.CloseCurrentPayPeriod("Closed");        // now attached to a report
            AddSale(NewLens(200m, "INV-OPEN"));
            MirrorAll();

            var summary = _analytics.GetSummary(_analytics.ResolveRange(AnalyticsService.CurrentPayPeriod));

            Assert.Equal(200m, summary.GrossSales);
            Assert.Equal(0m, summary.Returns);
        }

        [Fact]
        public void CustomRange_IsInclusiveOfWholeDays_AndChoosesAScale()
        {
            var range = _analytics.ResolveRange(AnalyticsService.Custom, new DateTime(2026, 3, 10), new DateTime(2026, 3, 1));

            Assert.Equal(new DateTime(2026, 3, 1), range.Start);              // reversed dates are swapped
            Assert.Equal(new DateTime(2026, 3, 10).AddDays(1).AddTicks(-1), range.End);
            Assert.Equal("day", range.Scale);

            Assert.Equal("week", _analytics.ResolveRange(AnalyticsService.Custom, new DateTime(2026, 1, 1), new DateTime(2026, 4, 30)).Scale);
            Assert.Equal("month", _analytics.ResolveRange(AnalyticsService.Custom, new DateTime(2025, 1, 1), new DateTime(2026, 4, 30)).Scale);
        }

        [Fact]
        public void CustomRange_OnlyCountsSalesInsideIt()
        {
            Store(new StandardSale { Category = "Lens", SalePrice = 100m, TransactionDate = new DateTime(2026, 3, 1, 9, 0, 0) });
            Store(new StandardSale { Category = "Lens", SalePrice = 200m, TransactionDate = new DateTime(2026, 3, 10, 23, 30, 0) });
            Store(new StandardSale { Category = "Lens", SalePrice = 400m, TransactionDate = new DateTime(2026, 3, 11, 0, 30, 0) });

            var summary = _analytics.GetSummary(_analytics.ResolveRange(AnalyticsService.Custom, new DateTime(2026, 3, 1), new DateTime(2026, 3, 10)));

            Assert.Equal(300m, summary.GrossSales);
        }
    }
}

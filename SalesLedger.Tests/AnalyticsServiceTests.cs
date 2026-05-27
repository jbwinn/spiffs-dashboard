using System;
using System.IO;
using System.Linq;
using Xunit;
using SalesLedger.Core.Models;
using SalesLedger.Core.Services;

namespace SalesLedger.Tests
{
    public class AnalyticsServiceTests : IDisposable
    {
        private readonly string _liteDbPath;
        private readonly string _duckDbPath;
        private readonly LiteDbService _liteDb;
        private readonly DuckDbService _duckDb;
        private readonly SyncPipeline _syncPipeline;
        private readonly AnalyticsService _analyticsService;

        public AnalyticsServiceTests()
        {
            _liteDbPath = Path.Combine(Path.GetTempPath(), $"test_lite_{Guid.NewGuid():N}.db");
            _duckDbPath = Path.Combine(Path.GetTempPath(), $"test_duck_{Guid.NewGuid():N}.duckdb");

            _liteDb = new LiteDbService(_liteDbPath);
            _duckDb = new DuckDbService(_duckDbPath);
            _syncPipeline = new SyncPipeline(_liteDb, _duckDb);

            _analyticsService = new AnalyticsService(_duckDb);
        }

        [Fact]
        public void GetSummary_CalculatesCorrectAggregations()
        {
            var now = DateTime.Now;

            // Inserts standard and warranty sales inside timeframe
            var standardSale = new StandardSale
            {
                InvoiceNumber = "INV-S1",
                ProductName = "Camera Standard",
                Category = "Cameras",
                SalePrice = 1000m,
                CalculatedCommission = 50.00m,
                TransactionDate = now.AddDays(-2),
                Status = PayoutStatus.Pending
            };
            var warrantySale = new WarrantySale
            {
                InvoiceNumber = "INV-W1",
                ProductName = "Warranty Plan",
                Category = "Accessories",
                SalePrice = 200m,
                CalculatedCommission = 25.00m,
                WarrantyTypeName = "1-Year Extension",
                ManufacturerPrice = 50m,
                TransactionDate = now.AddDays(-1),
                Status = PayoutStatus.Pending
            };
            var returnedSale = new StandardSale
            {
                InvoiceNumber = "INV-R1",
                ProductName = "Returned Camera",
                Category = "Cameras",
                SalePrice = 1500m,
                CalculatedCommission = 0.00m,
                TransactionDate = now.AddDays(-3),
                Status = PayoutStatus.ReturnedBeforePayout // Should be excluded from summary
            };

            _liteDb.Sales.Insert(standardSale);
            _liteDb.Sales.Insert(warrantySale);
            _liteDb.Sales.Insert(returnedSale);

            // Sync manually (since pipeline is asynchronous, we can call directly or wait)
            _duckDb.UpsertSale(standardSale);
            _duckDb.UpsertSale(warrantySale);
            _duckDb.UpsertSale(returnedSale);

            var summary = _analyticsService.GetSummary("Last 30 Days");

            Assert.Equal(1200.00m, summary.TotalSales);
            Assert.Equal(1, summary.TotalUnitsSold); // Only standardSale counts as 1. warrantySale = 0, returned = 0
            Assert.Equal(75.00m, summary.TotalCommission);
            Assert.Equal(1000.00m, summary.AverageSalePrice); // Average of standardSale only
        }

        [Fact]
        public void GetTrends_PopulatesContiguousBuckets_AndCategoryMetrics()
        {
            var now = DateTime.Now;
            var sale1 = new StandardSale
            {
                InvoiceNumber = "INV-T1",
                Category = "Cameras",
                SalePrice = 1000m,
                CalculatedCommission = 50.00m,
                TransactionDate = now.AddDays(-5),
                Status = PayoutStatus.Pending
            };
            var sale2 = new StandardSale
            {
                InvoiceNumber = "INV-T2",
                Category = "Lenses",
                SalePrice = 800m,
                CalculatedCommission = 64.00m,
                TransactionDate = now.AddDays(-2),
                Status = PayoutStatus.Pending
            };

            _liteDb.Sales.Insert(sale1);
            _liteDb.Sales.Insert(sale2);

            _duckDb.UpsertSale(sale1);
            _duckDb.UpsertSale(sale2);

            var trends = _analyticsService.GetTrends("Last 30 Days");

            Assert.NotEmpty(trends);
            // Verify category-specific quantities/revenue mapped correctly
            var cameraBucket = trends.FirstOrDefault(x => x.PeriodStart.Date == sale1.TransactionDate.Date);
            Assert.NotNull(cameraBucket);
            Assert.True(cameraBucket.CategoryBreakdown.ContainsKey("Cameras"));
            Assert.Equal(1000, cameraBucket.CategoryBreakdown["Cameras"].Revenue);

            var lensBucket = trends.FirstOrDefault(x => x.PeriodStart.Date == sale2.TransactionDate.Date);
            Assert.NotNull(lensBucket);
            Assert.True(lensBucket.CategoryBreakdown.ContainsKey("Lenses"));
            Assert.Equal(800, lensBucket.CategoryBreakdown["Lenses"].Revenue);
        }

        public void Dispose()
        {
            _syncPipeline.Stop();
            _liteDb.Dispose();
            _duckDb.Dispose();

            try { File.Delete(_liteDbPath); } catch {}
            try { File.Delete(_duckDbPath); } catch {}
            try { File.Delete(_duckDbPath + ".tmp"); } catch {}
        }
    }
}

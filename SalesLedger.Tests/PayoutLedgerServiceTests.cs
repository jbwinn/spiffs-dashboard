using System;
using System.IO;
using System.Linq;
using Xunit;
using SalesLedger.Core.Models;
using SalesLedger.Core.Services;

namespace SalesLedger.Tests
{
    public class PayoutLedgerServiceTests : IDisposable
    {
        private readonly string _liteDbPath;
        private readonly string _duckDbPath;
        private readonly LiteDbService _liteDb;
        private readonly DuckDbService _duckDb;
        private readonly SyncPipeline _syncPipeline;
        private readonly PayoutLedgerService _ledgerService;

        public PayoutLedgerServiceTests()
        {
            // Use unique temp files for testing
            _liteDbPath = Path.Combine(Path.GetTempPath(), $"test_lite_{Guid.NewGuid():N}.db");
            _duckDbPath = Path.Combine(Path.GetTempPath(), $"test_duck_{Guid.NewGuid():N}.duckdb");

            _liteDb = new LiteDbService(_liteDbPath);
            _duckDb = new DuckDbService(_duckDbPath);
            _syncPipeline = new SyncPipeline(_liteDb, _duckDb);

            _ledgerService = new PayoutLedgerService(_liteDb, _syncPipeline);
        }

        [Fact]
        public void ProcessReturn_PrePayout_SetsStatusToReturnedBeforePayout_AndZeroCommission()
        {
            var sale = new StandardSale
            {
                InvoiceNumber = "INV-100",
                ProductName = "Test Camera",
                SalePrice = 1200m,
                CalculatedCommission = 60.00m,
                Status = PayoutStatus.Pending
            };
            _liteDb.Sales.Insert(sale);

            _ledgerService.ProcessReturn(sale.Id);

            var updatedSale = _liteDb.Sales.FindById(sale.Id);
            Assert.NotNull(updatedSale);
            Assert.Equal(PayoutStatus.ReturnedBeforePayout, updatedSale.Status);
            Assert.Equal(0.00m, updatedSale.CalculatedCommission);
        }

        [Fact]
        public void ProcessReturn_PostPayout_CreatesNegativeReturnOffsetSale()
        {
            var originalSale = new StandardSale
            {
                InvoiceNumber = "INV-200",
                ProductName = "Paid Camera",
                SalePrice = 1500m,
                CalculatedCommission = 75.00m,
                Status = PayoutStatus.Paid
            };
            _liteDb.Sales.Insert(originalSale);

            _ledgerService.ProcessReturn(originalSale.Id);

            // Verify original sale remains unchanged
            var fetchedOriginal = _liteDb.Sales.FindById(originalSale.Id);
            Assert.Equal(PayoutStatus.Paid, fetchedOriginal.Status);

            // Verify a ReturnOffsetSale was inserted
            var offsets = _liteDb.Sales.Find(x => x.RecordType == SaleType.ReturnOffset).ToList();
            Assert.Single(offsets);

            var offset = (ReturnOffsetSale)offsets[0];
            Assert.Equal(originalSale.Id, offset.OriginalSaleId);
            Assert.Equal(-1500m, offset.SalePrice);
            Assert.Equal(-75.00m, offset.CalculatedCommission);
            Assert.Equal(PayoutStatus.Pending, offset.Status);
            Assert.Contains("RETURN OFFSET", offset.ProductName);
        }

        [Fact]
        public void CloseCurrentPayPeriod_LocksPendingSales_ToPaid()
        {
            var sale1 = new StandardSale
            {
                InvoiceNumber = "INV-301",
                SalePrice = 1000m,
                CalculatedCommission = 50.00m,
                Status = PayoutStatus.Pending
            };
            var sale2 = new StandardSale
            {
                InvoiceNumber = "INV-302",
                SalePrice = 2000m,
                CalculatedCommission = 100.00m,
                Status = PayoutStatus.Pending
            };
            _liteDb.Sales.Insert(sale1);
            _liteDb.Sales.Insert(sale2);

            var report = _ledgerService.CloseCurrentPayPeriod("July 2026");

            Assert.NotNull(report);
            Assert.Equal(150.00m, report.TotalCommissionCalculated);
            Assert.Equal(2, report.LockedSaleIds.Count);

            var updatedSale1 = _liteDb.Sales.FindById(sale1.Id);
            var updatedSale2 = _liteDb.Sales.FindById(sale2.Id);

            Assert.Equal(PayoutStatus.Paid, updatedSale1.Status);
            Assert.Equal(report.Id, updatedSale1.AssociatedReportId);

            Assert.Equal(PayoutStatus.Paid, updatedSale2.Status);
            Assert.Equal(report.Id, updatedSale2.AssociatedReportId);
        }

        public void Dispose()
        {
            _syncPipeline.Stop();
            _liteDb.Dispose();
            _duckDb.Dispose();

            // Try clean files
            try { File.Delete(_liteDbPath); } catch {}
            try { File.Delete(_duckDbPath); } catch {}
            try { File.Delete(_duckDbPath + ".tmp"); } catch {}
        }
    }
}

using SalesLedger.Core.Models;
using SalesLedger.Core.Services;

namespace SalesLedger.Tests
{
    /// <summary>
    /// Fresh LiteDB + DuckDB files per test with all services wired up. Default settings apply:
    /// Used 3%, Warranty 10% of net, eBay 10%, Lens 8%, Mirrorless 5%.
    /// </summary>
    public abstract class LedgerTestBase : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), $"ledger_test_{Guid.NewGuid():N}");

        protected LiteDbService Lite { get; }
        protected DuckDbService Duck { get; }
        protected SyncPipeline Sync { get; }
        protected CommissionProcessor Processor { get; } = new();
        protected PayoutLedgerService Payout { get; }
        protected ReceiptService Receipts { get; }
        protected CsvImportService CsvImport { get; }
        protected ReportService Reports { get; }

        /// <summary>This test's reports folder (inside its throwaway folder, never the real Documents folder).</summary>
        protected string ReportsFolder => Path.Combine(_dir, "reports");

        protected LedgerTestBase()
        {
            Lite = new LiteDbService(Path.Combine(_dir, "lite.db"));
            Duck = new DuckDbService(Path.Combine(_dir, "duck.duckdb"));
            Sync = new SyncPipeline(Lite, Duck);
            Payout = new PayoutLedgerService(Lite, Sync, Processor);
            Receipts = new ReceiptService(Lite, Sync, Processor, Payout);
            CsvImport = new CsvImportService(Lite, Sync, Processor);
            Reports = new ReportService(Lite, Payout, new ReportGenerator());
            Reports.SetReportsFolder(ReportsFolder);
        }

        protected List<CommissionRule> Rules => Lite.GetUserSettings().ActiveRules ?? [];

        /// <summary>Inserts a sale with its commission calculated by the current rules.</summary>
        protected T AddSale<T>(T sale, PayoutStatus status = PayoutStatus.Pending) where T : SaleRecord
        {
            Processor.ApplyCommission(sale, Rules);
            sale.Status = status;
            Lite.Sales.Insert(sale);
            return sale;
        }

        protected StandardSale NewLens(decimal price, string invoice = "INV-1", DateTime? date = null) => new()
        {
            InvoiceNumber = invoice,
            Sku = "LENS-1",
            ProductName = "Test Lens",
            Category = "Lens",
            SalePrice = price,
            IsUsedGear = false,
            TransactionDate = date ?? DateTime.Now.AddDays(-5)
        };

        protected SaleRecord Reload(SaleRecord sale) => Lite.Sales.FindById(sale.Id);

        protected List<SaleRecord> ReturnLinesFor(SaleRecord sale) => Payout.GetReturnLines(sale.Id);

        protected void SetRule(string ruleName, Action<CommissionRule> change)
        {
            var settings = Lite.GetUserSettings();
            change(settings.ActiveRules!.Single(r => r.RuleName == ruleName));
            Lite.SaveUserSettings(settings);
        }

        public void Dispose()
        {
            Sync.Stop();
            Lite.Dispose();
            Duck.Dispose();
            try { Directory.Delete(_dir, recursive: true); } catch (Exception) { /* ignore */ }
            GC.SuppressFinalize(this);
        }
    }
}

using System;
using System.Linq;
using SalesLedger.Core.Models;

namespace SalesLedger.Core.Services
{
    public class PayoutLedgerService
    {
        private readonly LiteDbService _dbService;
        private readonly SyncPipeline _syncPipeline;

        public PayoutLedgerService(LiteDbService dbService, SyncPipeline syncPipeline)
        {
            _dbService = dbService ?? throw new ArgumentNullException(nameof(dbService));
            _syncPipeline = syncPipeline ?? throw new ArgumentNullException(nameof(syncPipeline));
        }

        public void ProcessReturn(Guid originalSaleId)
        {
            var originalSale = _dbService.Sales.FindById(originalSaleId);
            if (originalSale != null)
            {
                ProcessReturn(originalSale);
            }
        }

        public void ProcessReturn(SaleRecord originalSale)
        {
            if (originalSale.Status == PayoutStatus.Pending)
            {
                // Branch A: Pre-payout modification
                originalSale.Status = PayoutStatus.ReturnedBeforePayout;
                originalSale.CalculatedCommission = 0.00m;
                _dbService.Sales.Update(originalSale);
                
                // Mirror to DuckDB
                _syncPipeline.QueueUpsert(originalSale);
            }
            else if (originalSale.Status == PayoutStatus.Paid)
            {
                // Check if this sale has already been returned (post-payout offset check)
                var existingOffset = _dbService.Sales.Find(x => 
                    x.RecordType == SaleType.ReturnOffset && 
                    ((ReturnOffsetSale)x).OriginalSaleId == originalSale.Id).Any();
                
                if (existingOffset)
                {
                    // Already returned, block duplicate offset
                    return;
                }

                // Branch B: Post-payout legacy balance ledger offset injection
                var offset = new ReturnOffsetSale
                {
                    Id = Guid.NewGuid(),
                    OriginalSaleId = originalSale.Id,
                    TransactionDate = DateTime.UtcNow, // Belongs to current open calculation pool
                    InvoiceNumber = originalSale.InvoiceNumber,
                    Sku = originalSale.Sku,
                    ProductName = $"[RETURN OFFSET] - {originalSale.ProductName}",
                    Category = originalSale.Category,
                    SalePrice = -originalSale.SalePrice, // Balanced accounting negative entry
                    CalculatedCommission = -originalSale.CalculatedCommission, // Balances total upcoming payout
                    Status = PayoutStatus.Pending
                };
                
                _dbService.Sales.Insert(offset);
                
                // Mirror to DuckDB
                _syncPipeline.QueueUpsert(offset);
            }
        }

        public PayoutReport CloseCurrentPayPeriod(string reportName)
        {
            var pendingSales = _dbService.Sales.Find(x => x.Status == PayoutStatus.Pending).ToList();

            var report = new PayoutReport
            {
                Id = Guid.NewGuid(),
                ReportGeneratedTimestamp = DateTime.UtcNow,
                ReportName = reportName,
                TotalCommissionCalculated = pendingSales.Sum(s => s.CalculatedCommission),
                LockedSaleIds = pendingSales.Select(s => s.Id).ToList()
            };

            // Transition open items permanently into locked history logs
            foreach (var sale in pendingSales)
            {
                sale.Status = PayoutStatus.Paid;
                sale.AssociatedReportId = report.Id;
                _dbService.Sales.Update(sale);
                
                // Sync changes to DuckDB
                _syncPipeline.QueueUpsert(sale);
            }

            _dbService.Reports.Insert(report);
            return report;
        }
    }
}

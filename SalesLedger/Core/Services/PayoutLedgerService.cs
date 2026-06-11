using System;
using System.Linq;
using SalesLedger.Core.Models;

namespace SalesLedger.Core.Services
{
    public class PayoutLedgerService(LiteDbService dbService, SyncPipeline syncPipeline)
    {
        private readonly LiteDbService _dbService = dbService ?? throw new ArgumentNullException(nameof(dbService));
        private readonly SyncPipeline _syncPipeline = syncPipeline ?? throw new ArgumentNullException(nameof(syncPipeline));

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
                    x.IsReturn && 
                    x.OriginalSaleId == originalSale.Id).Any();
                
                if (existingOffset)
                {
                    // Already returned, block duplicate offset
                    return;
                }

                // Branch B: Post-payout legacy balance ledger offset injection
                // Create a return record of the exact same concrete class as the original sale
                SaleRecord offset;
                if (originalSale is StandardSale std)
                {
                    offset = new StandardSale
                    {
                        IsUsedGear = std.IsUsedGear
                    };
                }
                else if (originalSale is EbaySale ebay)
                {
                    offset = new EbaySale
                    {
                        IsUsedGear = ebay.IsUsedGear
                    };
                }
                else if (originalSale is WarrantySale war)
                {
                    offset = new WarrantySale
                    {
                        WarrantyTypeName = war.WarrantyTypeName,
                        ManufacturerPrice = -war.ManufacturerPrice
                    };
                }
                else
                {
                    offset = new StandardSale();
                }

                offset.Id = Guid.NewGuid();
                offset.IsReturn = true;
                offset.OriginalSaleId = originalSale.Id;
                offset.TransactionDate = DateTime.Now;
                offset.InvoiceNumber = originalSale.InvoiceNumber;
                offset.Sku = originalSale.Sku;
                offset.ProductName = $"[RETURN] - {originalSale.ProductName}";
                offset.Category = originalSale.Category;
                offset.SalePrice = -originalSale.SalePrice;
                offset.CalculatedCommission = -originalSale.CalculatedCommission;
                offset.Status = PayoutStatus.Pending;
                
                _dbService.Sales.Insert(offset);
                
                // Mirror to DuckDB
                _syncPipeline.QueueUpsert(offset);
            }
        }

        public PayoutReport CloseCurrentPayPeriod(string reportName)
        {
            var pendingSales = _dbService.Sales.Find(x => x.Status == PayoutStatus.Pending).ToList();
            var returnedBeforePayoutSales = _dbService.Sales.Find(x => x.Status == PayoutStatus.ReturnedBeforePayout && x.AssociatedReportId == null).ToList();

            var allPeriodSales = pendingSales.Concat(returnedBeforePayoutSales).ToList();

            var report = new PayoutReport
            {
                Id = Guid.NewGuid(),
                ReportGeneratedTimestamp = DateTime.UtcNow,
                ReportName = reportName,
                TotalCommissionCalculated = pendingSales.Sum(s => s.CalculatedCommission), // Exclude ReturnedBeforePayout sales (whose commission is 0.00m)
                LockedSaleIds = allPeriodSales.Select(s => s.Id).ToList()
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

            foreach (var sale in returnedBeforePayoutSales)
            {
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

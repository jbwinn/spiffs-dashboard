using System;
using System.Collections.Generic;
using System.Linq;
using SalesLedger.Core.Models;

namespace SalesLedger.Core.Services
{
    /// <summary>
    /// What a proposed return would do, computed without saving anything.
    /// </summary>
    public sealed class ReturnPreview
    {
        public decimal OriginalPrice { get; init; }
        public decimal AlreadyRefunded { get; init; }
        public decimal Remaining { get; init; }

        /// <summary>Positive amount of commission the return takes back.</summary>
        public decimal CommissionClawback { get; init; }

        /// <summary>True when a full return of a pending sale simply marks it ReturnedBeforePayout.</summary>
        public bool MarksSaleReturnedBeforePayout { get; init; }

        public string? Error { get; init; }
        public bool IsValid => Error == null;
    }

    public class PayoutLedgerService(LiteDbService dbService, SyncPipeline syncPipeline, CommissionProcessor? commissionProcessor = null)
    {
        private readonly LiteDbService _dbService = dbService ?? throw new ArgumentNullException(nameof(dbService));
        private readonly SyncPipeline _syncPipeline = syncPipeline ?? throw new ArgumentNullException(nameof(syncPipeline));
        private readonly CommissionProcessor _processor = commissionProcessor ?? new CommissionProcessor();

        // ---------------------------------------------------------------------
        // Returns
        // ---------------------------------------------------------------------

        /// <summary>All return lines recorded against a sale, oldest first.</summary>
        public List<SaleRecord> GetReturnLines(Guid originalSaleId)
        {
            return _dbService.Sales
                .Find(x => x.IsReturn && x.OriginalSaleId == originalSaleId)
                .OrderBy(x => x.TransactionDate)
                .ThenBy(x => x.Id)
                .ToList();
        }

        /// <summary>
        /// Validates a return and works out its commission effect without saving anything.
        /// Pass <paramref name="editingLineId"/> when changing an existing return line.
        /// </summary>
        public ReturnPreview PreviewReturn(SaleRecord original, decimal refundAmount, DateTime? returnDate = null, Guid? editingLineId = null)
        {
            if (original.IsReturn || original.RecordType == SaleType.ReturnOffset)
            {
                return new ReturnPreview { Error = "A return line can't itself be returned." };
            }

            if (original.Status == PayoutStatus.ReturnedBeforePayout)
            {
                return new ReturnPreview { Error = "This sale has already been returned." };
            }

            var otherLines = GetReturnLines(original.Id).Where(l => l.Id != editingLineId).ToList();
            var alreadyRefunded = otherLines.Sum(l => -l.SalePrice);
            var remaining = original.SalePrice - alreadyRefunded;

            string? error = null;
            if (original.SalePrice <= 0)
            {
                error = "This sale has no price to refund.";
            }
            else if (refundAmount <= 0)
            {
                error = "The refund amount must be greater than $0.00.";
            }
            else if (refundAmount > remaining)
            {
                error = remaining <= 0
                    ? "This sale has already been fully refunded."
                    : $"The refund can't be more than the remaining {remaining:C}.";
            }

            if (error != null)
            {
                return new ReturnPreview
                {
                    OriginalPrice = original.SalePrice,
                    AlreadyRefunded = alreadyRefunded,
                    Remaining = remaining,
                    Error = error
                };
            }

            bool marksReturned = editingLineId == null
                                 && original.Status == PayoutStatus.Pending
                                 && otherLines.Count == 0
                                 && refundAmount == original.SalePrice;

            decimal clawback;
            if (marksReturned)
            {
                clawback = original.CalculatedCommission;
            }
            else
            {
                // Simulate the full set of return lines in date order, with this one included
                var date = returnDate ?? DateTime.Now;
                var slots = otherLines
                    .Select(l => (Date: l.TransactionDate, Id: l.Id, Slot: ToSlot(l), IsCandidate: false))
                    .Append((Date: date, Id: editingLineId ?? Guid.Empty, Slot: new ReturnSlot(refundAmount, null), IsCandidate: true))
                    .OrderBy(s => s.Date)
                    .ThenBy(s => s.IsCandidate) // a new return on the same timestamp goes last
                    .ThenBy(s => s.Id)
                    .ToList();

                var clawbacks = ComputeClawbacks(original, IsFlatRate(original), slots.Select(s => s.Slot).ToList());
                clawback = clawbacks[slots.FindIndex(s => s.IsCandidate)];
            }

            return new ReturnPreview
            {
                OriginalPrice = original.SalePrice,
                AlreadyRefunded = alreadyRefunded,
                Remaining = remaining,
                CommissionClawback = clawback,
                MarksSaleReturnedBeforePayout = marksReturned
            };
        }

        public void ProcessReturn(Guid originalSaleId)
        {
            var originalSale = _dbService.Sales.FindById(originalSaleId);
            if (originalSale != null)
            {
                ProcessReturn(originalSale);
            }
        }

        /// <summary>
        /// Records a return. With no amount, refunds whatever is left of the sale.
        /// A full return of a pending sale with no earlier returns marks it ReturnedBeforePayout;
        /// anything else (partial returns, returns of paid sales) adds a return line in the current period.
        /// Throws InvalidOperationException if the return isn't valid.
        /// </summary>
        public void ProcessReturn(SaleRecord originalSale, decimal? refundAmount = null, DateTime? returnDate = null)
        {
            var original = _dbService.Sales.FindById(originalSale.Id) ?? originalSale;
            var date = returnDate ?? DateTime.Now;

            var refund = refundAmount ?? original.SalePrice - GetReturnLines(original.Id).Sum(l => -l.SalePrice);
            var preview = PreviewReturn(original, refund, date);
            if (!preview.IsValid)
            {
                throw new InvalidOperationException(preview.Error);
            }

            if (preview.MarksSaleReturnedBeforePayout)
            {
                original.Status = PayoutStatus.ReturnedBeforePayout;
                original.CalculatedCommission = 0.00m;
                _dbService.Sales.Update(original);
                _syncPipeline.QueueUpsert(original);
                return;
            }

            // Return line of the same concrete type as the original sale
            SaleRecord line = original switch
            {
                StandardSale std => new StandardSale { IsUsedGear = std.IsUsedGear },
                EbaySale ebay => new EbaySale { IsUsedGear = ebay.IsUsedGear },
                WarrantySale war => new WarrantySale { WarrantyTypeName = war.WarrantyTypeName },
                _ => new StandardSale()
            };

            line.Id = Guid.NewGuid();
            line.IsReturn = true;
            line.OriginalSaleId = original.Id;
            line.TransactionDate = date;
            line.InvoiceNumber = original.InvoiceNumber;
            line.Sku = original.Sku;
            line.ProductName = $"[RETURN] - {original.ProductName}";
            line.Category = original.Category;
            line.SalePrice = -refund;
            line.Status = PayoutStatus.Pending;
            line.AppliedCalculationType = original.AppliedCalculationType;

            _dbService.Sales.Insert(line);

            // Works out the clawback for the new line (and keeps sibling lines consistent)
            RebalanceReturnLines(original);
        }

        /// <summary>
        /// Changes the refund amount (and optionally the date) of an existing, still-pending return line.
        /// Throws InvalidOperationException if the change isn't valid.
        /// </summary>
        public void UpdateReturnLine(Guid returnLineId, decimal refundAmount, DateTime? returnDate = null)
        {
            var line = _dbService.Sales.FindById(returnLineId);
            if (line == null || !line.IsReturn || line.OriginalSaleId == null)
            {
                throw new InvalidOperationException("Return line not found.");
            }

            if (line.Status != PayoutStatus.Pending)
            {
                throw new InvalidOperationException("This return has already been paid out and can't be changed.");
            }

            var original = _dbService.Sales.FindById(line.OriginalSaleId.Value)
                           ?? throw new InvalidOperationException("The original sale for this return no longer exists.");

            var date = returnDate ?? line.TransactionDate;
            var preview = PreviewReturn(original, refundAmount, date, line.Id);
            if (!preview.IsValid)
            {
                throw new InvalidOperationException(preview.Error);
            }

            line.SalePrice = -refundAmount;
            line.TransactionDate = date;
            _dbService.Sales.Update(line);

            RebalanceReturnLines(original);
        }

        /// <summary>
        /// Recomputes the clawback on every pending return line of a sale, in date order.
        /// Paid (locked) return lines keep their amounts and count towards what has already been taken back.
        /// </summary>
        public void RebalanceReturnLines(SaleRecord original)
        {
            var lines = GetReturnLines(original.Id);
            if (lines.Count == 0)
            {
                return;
            }

            var clawbacks = ComputeClawbacks(original, IsFlatRate(original), lines.Select(ToSlot).ToList());
            var completes = ComputeCompletesRefund(original, lines);

            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                if (line.Status != PayoutStatus.Pending)
                {
                    continue;
                }

                line.CalculatedCommission = -clawbacks[i];
                line.CompletesRefund = completes[i];

                if (line is WarrantySale warLine && original is WarrantySale warOriginal && original.SalePrice != 0)
                {
                    warLine.ManufacturerPrice = Math.Round(warOriginal.ManufacturerPrice * line.SalePrice / original.SalePrice, 2);
                }

                _dbService.Sales.Update(line);
                _syncPipeline.QueueUpsert(line);
            }
        }

        // ---------------------------------------------------------------------
        // Deletes
        // ---------------------------------------------------------------------

        /// <summary>
        /// Deletes an unpaid sale. Deleting a sale also deletes its return lines; deleting a return line
        /// re-balances the remaining returns on that sale. Paid records can't be deleted.
        /// </summary>
        public void DeleteSale(Guid saleId)
        {
            var sale = _dbService.Sales.FindById(saleId);
            if (sale == null)
            {
                return;
            }

            if (sale.Status == PayoutStatus.Paid)
            {
                throw new InvalidOperationException("Paid records are locked and can't be deleted.");
            }

            _dbService.Sales.Delete(sale.Id);
            _syncPipeline.QueueDelete(sale.Id);

            if (sale.IsReturn)
            {
                if (sale.OriginalSaleId is { } originalId && _dbService.Sales.FindById(originalId) is { } original)
                {
                    RebalanceReturnLines(original);
                }
                return;
            }

            foreach (var line in GetReturnLines(sale.Id).Where(l => l.Status != PayoutStatus.Paid))
            {
                _dbService.Sales.Delete(line.Id);
                _syncPipeline.QueueDelete(line.Id);
            }
        }

        // ---------------------------------------------------------------------
        // Rule changes
        // ---------------------------------------------------------------------

        /// <summary>
        /// Re-runs the commission rules on every pending sale in the current period (return lines excluded,
        /// they follow their original sale). Returns the number of sales whose commission changed.
        /// </summary>
        public int RecalculatePendingCommissions()
        {
            var rules = _dbService.GetUserSettings().ActiveRules ?? [];
            var pending = _dbService.Sales
                .Find(x => x.Status == PayoutStatus.Pending && !x.IsReturn)
                .ToList();

            int changed = 0;
            foreach (var sale in pending)
            {
                var oldCommission = sale.CalculatedCommission;
                var oldType = sale.AppliedCalculationType;

                _processor.ApplyCommission(sale, rules);

                if (oldCommission == sale.CalculatedCommission && oldType == sale.AppliedCalculationType)
                {
                    continue;
                }

                _dbService.Sales.Update(sale);
                _syncPipeline.QueueUpsert(sale);
                RebalanceReturnLines(sale);
                changed++;
            }

            return changed;
        }

        // ---------------------------------------------------------------------
        // Period close
        // ---------------------------------------------------------------------

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

        // ---------------------------------------------------------------------
        // Clawback math
        // ---------------------------------------------------------------------

        /// <summary>A return line for clawback purposes: its refund, and its clawback if it is locked (paid).</summary>
        internal readonly record struct ReturnSlot(decimal Refund, decimal? LockedClawback);

        private static ReturnSlot ToSlot(SaleRecord line) =>
            new(-line.SalePrice, line.Status == PayoutStatus.Pending ? null : -line.CalculatedCommission);

        /// <summary>
        /// Clawback for each return line, processed in order:
        /// - Flat-rate commission: the first return takes back the full amount, later returns take back nothing.
        /// - Percentage commission: proportional to the refund; the return that completes the refund
        ///   takes back whatever is left so rounding never strands pennies.
        /// The total taken back never exceeds the original commission.
        /// </summary>
        internal static decimal[] ComputeClawbacks(SaleRecord original, bool isFlatRate, IReadOnlyList<ReturnSlot> slots)
        {
            var result = new decimal[slots.Count];
            decimal priorRefund = 0m;
            decimal priorClawback = 0m;

            for (int i = 0; i < slots.Count; i++)
            {
                var slot = slots[i];
                decimal clawback;

                if (slot.LockedClawback is { } locked)
                {
                    clawback = locked;
                }
                else
                {
                    var remainingCommission = Math.Max(0m, original.CalculatedCommission - priorClawback);

                    if (isFlatRate || original.SalePrice <= 0 || priorRefund + slot.Refund >= original.SalePrice)
                    {
                        clawback = remainingCommission;
                    }
                    else
                    {
                        var proportional = Math.Round(original.CalculatedCommission * slot.Refund / original.SalePrice, 2);
                        clawback = Math.Min(remainingCommission, proportional);
                    }
                }

                result[i] = clawback;
                priorRefund += slot.Refund;
                priorClawback += clawback;
            }

            return result;
        }

        /// <summary>For each return line (in date order): does it bring the refunds up to the full sale price?</summary>
        private static bool[] ComputeCompletesRefund(SaleRecord original, IReadOnlyList<SaleRecord> orderedLines)
        {
            var result = new bool[orderedLines.Count];
            if (original.SalePrice <= 0)
            {
                return result;
            }

            decimal refunded = 0m;
            for (int i = 0; i < orderedLines.Count; i++)
            {
                var before = refunded;
                refunded += -orderedLines[i].SalePrice;
                result[i] = before < original.SalePrice && refunded >= original.SalePrice;
            }
            return result;
        }

        /// <summary>
        /// Sets <see cref="SaleRecord.CompletesRefund"/> on every return line (paid ones included: it's derived
        /// information, not money). Fixes up return lines recorded before the flag existed. Returns lines changed.
        /// Run at startup, before the analytics store is rebuilt.
        /// </summary>
        public int RefreshReturnCompletionFlags()
        {
            int changed = 0;
            var linesByOriginal = _dbService.Sales.Find(x => x.IsReturn && x.OriginalSaleId != null)
                .GroupBy(x => x.OriginalSaleId!.Value);

            foreach (var group in linesByOriginal)
            {
                var original = _dbService.Sales.FindById(group.Key);
                if (original == null) continue;

                var ordered = group.OrderBy(x => x.TransactionDate).ThenBy(x => x.Id).ToList();
                var completes = ComputeCompletesRefund(original, ordered);
                for (int i = 0; i < ordered.Count; i++)
                {
                    if (ordered[i].CompletesRefund == completes[i]) continue;
                    ordered[i].CompletesRefund = completes[i];
                    _dbService.Sales.Update(ordered[i]);
                    changed++;
                }
            }

            return changed;
        }

        private bool IsFlatRate(SaleRecord original)
        {
            if (original.AppliedCalculationType is { } type)
            {
                return type == PayoutType.FlatRate;
            }

            // Older records don't store their payout method: infer it from the current rules
            var rules = _dbService.GetUserSettings().ActiveRules ?? [];
            return _processor.FindMatchingRule(original, rules)?.CalculationType == PayoutType.FlatRate;
        }
    }
}

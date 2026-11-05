using System;
using System.Collections.Generic;
using System.Linq;
using SalesLedger.Core.Models;

namespace SalesLedger.Core.Services
{
    /// <summary>A receipt (all sales sharing an invoice number), split into what can and can't be edited.</summary>
    public sealed class ReceiptSnapshot
    {
        /// <summary>Pending, non-return sales: fully editable.</summary>
        public List<SaleRecord> EditableItems { get; init; } = [];

        /// <summary>Paid sales, sales returned before payout, and return lines: shown for context only.</summary>
        public List<SaleRecord> ReadOnlyItems { get; init; } = [];

        /// <summary>The invoice number as stored (lookups ignore case).</summary>
        public string InvoiceNumber { get; init; } = string.Empty;

        /// <summary>The receipt's date: its earliest sale (return lines excluded). Null if the receipt doesn't exist.</summary>
        public DateTime? ReceiptDate { get; init; }

        public bool Exists => EditableItems.Count > 0 || ReadOnlyItems.Count > 0;
    }

    /// <summary>A product name used before, with what it was last sold as (for autofill).</summary>
    public sealed record ProductSuggestion(string Name, bool IsWarranty, string Category, string WarrantyTypeName);

    /// <summary>A return chosen in the receipt dialog, applied when the receipt is saved.</summary>
    public sealed record StagedReturn(Guid SaleId, decimal RefundAmount, DateTime ReturnDate);

    /// <summary>
    /// Loads and saves multi-item receipts. A receipt has no record of its own: it is every sale
    /// with the same invoice number. Only pending, non-return items are ever modified here; paid
    /// items can only be returned, which goes through <see cref="PayoutLedgerService.ProcessReturn(SaleRecord, decimal?, DateTime?)"/>.
    /// </summary>
    public class ReceiptService(LiteDbService db, SyncPipeline sync, CommissionProcessor processor, PayoutLedgerService payout)
    {
        public static bool IsEditable(SaleRecord sale) =>
            sale.Status == PayoutStatus.Pending && !sale.IsReturn && sale.RecordType != SaleType.ReturnOffset;

        /// <summary>Loads every sale on an invoice. Matching ignores case and surrounding spaces ("inv-12" finds "INV-12").</summary>
        public ReceiptSnapshot LoadReceipt(string invoiceNumber)
        {
            var key = invoiceNumber.Trim();
            if (key.Length == 0)
            {
                return new ReceiptSnapshot { InvoiceNumber = key };
            }

            var sales = db.Sales.FindAll()
                .Where(x => string.Equals(x.InvoiceNumber.Trim(), key, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.TransactionDate)
                .ThenBy(x => x.IsReturn)
                .ToList();

            var dated = sales.Where(s => !s.IsReturn).Select(s => s.TransactionDate).ToList();

            return new ReceiptSnapshot
            {
                EditableItems = sales.Where(IsEditable).ToList(),
                ReadOnlyItems = sales.Where(s => !IsEditable(s)).ToList(),
                InvoiceNumber = sales.FirstOrDefault()?.InvoiceNumber ?? key,
                ReceiptDate = dated.Count > 0 ? dated.Min() : sales.FirstOrDefault()?.TransactionDate
            };
        }

        /// <summary>
        /// Product names entered before, most-used first, each with the category (or warranty type) it was last
        /// sold under. Built from the ledger itself, so new names appear automatically. Return lines are excluded.
        /// </summary>
        public List<ProductSuggestion> GetProductSuggestions()
        {
            return db.Sales.Find(x => !x.IsReturn)
                .Where(s => s.RecordType != SaleType.ReturnOffset && !string.IsNullOrWhiteSpace(s.ProductName))
                .GroupBy(s => (Name: s.ProductName.Trim().ToUpperInvariant(), IsWarranty: s is WarrantySale))
                .Select(g =>
                {
                    var latest = g.OrderByDescending(s => s.TransactionDate).First();
                    return new
                    {
                        Count = g.Count(),
                        Latest = latest.TransactionDate,
                        Suggestion = new ProductSuggestion(
                            latest.ProductName.Trim(),
                            g.Key.IsWarranty,
                            latest.Category,
                            (latest as WarrantySale)?.WarrantyTypeName ?? string.Empty)
                    };
                })
                .OrderByDescending(x => x.Count)
                .ThenByDescending(x => x.Latest)
                .Select(x => x.Suggestion)
                .ToList();
        }

        /// <summary>
        /// Checks staged returns against the items as they will be saved. Returns an error message, or null if all are valid.
        /// </summary>
        public string? ValidateStagedReturns(IReadOnlyList<SaleRecord> editableItems, IEnumerable<StagedReturn> stagedReturns)
        {
            foreach (var staged in stagedReturns)
            {
                var sale = editableItems.FirstOrDefault(i => i.Id == staged.SaleId) ?? db.Sales.FindById(staged.SaleId);
                if (sale == null)
                {
                    continue;
                }

                var preview = payout.PreviewReturn(sale, staged.RefundAmount, staged.ReturnDate);
                if (!preview.IsValid)
                {
                    return $"Return on '{sale.ProductName}': {preview.Error}";
                }
            }

            return null;
        }

        /// <summary>
        /// Saves a receipt: upserts the editable items (stamping the invoice number, recalculating commission),
        /// deletes removed items, then applies staged returns. Each item keeps its own TransactionDate, so an
        /// item added later can carry today's date while the rest keep the receipt's date.
        /// Locked records are never modified.
        /// Throws InvalidOperationException (before writing anything) if a staged return is invalid.
        /// </summary>
        public void SaveReceipt(
            string invoiceNumber,
            IReadOnlyList<SaleRecord> editableItems,
            IEnumerable<Guid> removedItemIds,
            IReadOnlyList<StagedReturn> stagedReturns)
        {
            var validationError = ValidateStagedReturns(editableItems, stagedReturns);
            if (validationError != null)
            {
                throw new InvalidOperationException(validationError);
            }

            var rules = db.GetUserSettings().ActiveRules ?? [];

            foreach (var item in editableItems)
            {
                var existing = db.Sales.FindById(item.Id);
                if (existing != null && !IsEditable(existing))
                {
                    continue; // Never overwrite a record that has been locked or returned
                }

                item.InvoiceNumber = invoiceNumber;
                item.Status = PayoutStatus.Pending;
                processor.ApplyCommission(item, rules);

                if (existing != null)
                {
                    db.Sales.Update(item);
                }
                else
                {
                    db.Sales.Insert(item);
                }
                sync.QueueUpsert(item);

                if (existing != null)
                {
                    // Price or commission may have changed: keep any partial returns consistent
                    payout.RebalanceReturnLines(item);
                }
            }

            foreach (var id in removedItemIds)
            {
                var sale = db.Sales.FindById(id);
                if (sale != null && IsEditable(sale))
                {
                    payout.DeleteSale(id);
                }
            }

            foreach (var staged in stagedReturns)
            {
                var sale = db.Sales.FindById(staged.SaleId);
                if (sale != null)
                {
                    payout.ProcessReturn(sale, staged.RefundAmount, staged.ReturnDate);
                }
            }
        }
    }
}

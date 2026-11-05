using System;
using System.Collections.Generic;
using System.Linq;
using SalesLedger.Core.Models;

namespace SalesLedger.Core.Services
{
    /// <summary>An earlier sale with the same SKU, and whether that item came back.</summary>
    public sealed record SkuMatch(SaleRecord Sale, bool IsReturned, string ReturnDetail)
    {
        /// <summary>e.g. "Oct 2, 2026 · INV-881 · Canon 24-70 · $1,299.00 · Pending" (+ return detail).</summary>
        public string Description =>
            $"{Sale.TransactionDate:MMM d, yyyy} · {Sale.InvoiceNumber} · {Sale.ProductName} · {Sale.SalePrice:C} · " +
            (string.IsNullOrEmpty(ReturnDetail) ? StatusText(Sale) : ReturnDetail);

        private static string StatusText(SaleRecord s) => s.Status == PayoutStatus.Paid ? "Paid" : "Pending, not returned";
    }

    /// <summary>The warning to show for a duplicate SKU.</summary>
    public sealed record SkuWarning(string Title, string Message, bool IsResaleOfReturnedItem);

    /// <summary>
    /// Used and eBay items are one of a kind, so entering a SKU that has been sold before needs a second look:
    /// - every earlier sale of it was returned → a notice that the item is being sold again;
    /// - otherwise → a warning showing the other sale(s), since this may be a double entry.
    /// Both ask for confirmation; neither blocks.
    /// </summary>
    public class SkuCheckService(LiteDbService db, PayoutLedgerService payout)
    {
        /// <summary>Which entries are checked: used standard sales and all eBay sales (not new gear or warranty plans).</summary>
        public static bool ShouldCheck(SaleType type, bool isUsedGear) =>
            type == SaleType.Ebay || (type == SaleType.Standard && isUsedGear);

        public static bool ShouldCheck(SaleRecord sale) =>
            !sale.IsReturn && sale switch
            {
                EbaySale => true,
                StandardSale std => std.IsUsedGear,
                _ => false
            };

        /// <summary>
        /// Every earlier sale with this SKU (case and surrounding spaces ignored), newest first, whatever its type
        /// or status. Return lines and blank SKUs never match.
        /// </summary>
        public List<SkuMatch> FindPriorSales(string sku, IEnumerable<Guid>? excludeIds = null)
        {
            var key = sku.Trim();
            if (key.Length == 0)
            {
                return [];
            }

            var exclude = excludeIds?.ToHashSet() ?? [];

            return db.Sales.Find(x => !x.IsReturn)
                .Where(s => s.RecordType != SaleType.ReturnOffset
                            && !exclude.Contains(s.Id)
                            && string.Equals(s.Sku.Trim(), key, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(s => s.TransactionDate)
                .Select(Describe)
                .ToList();
        }

        private SkuMatch Describe(SaleRecord sale)
        {
            if (sale.Status == PayoutStatus.ReturnedBeforePayout)
            {
                return new SkuMatch(sale, IsReturned: true, "Returned before payout");
            }

            var lines = payout.GetReturnLines(sale.Id);
            var refunded = lines.Sum(l => -l.SalePrice);
            if (refunded <= 0)
            {
                return new SkuMatch(sale, IsReturned: false, string.Empty);
            }

            if (sale.SalePrice > 0 && refunded >= sale.SalePrice)
            {
                var returnedOn = lines.First(l => l.CompletesRefund || l == lines[^1]).TransactionDate;
                return new SkuMatch(sale, IsReturned: true, $"{sale.Status}, returned {returnedOn:MMM d, yyyy}");
            }

            return new SkuMatch(sale, IsReturned: false, $"{sale.Status}, partially refunded ({refunded:C}), customer kept it");
        }

        /// <summary>
        /// The warning for a SKU, or null if there's nothing to say. <paramref name="otherEntriesNotYetSaved"/> are
        /// descriptions of other unsaved lines (e.g. on the same receipt) with this SKU; those always count as active.
        /// </summary>
        public static SkuWarning? BuildWarning(string sku, IReadOnlyList<SkuMatch> matches, IReadOnlyList<string>? otherEntriesNotYetSaved = null)
        {
            var unsaved = otherEntriesNotYetSaved ?? [];
            if (matches.Count == 0 && unsaved.Count == 0)
            {
                return null;
            }

            var key = sku.Trim();
            var allReturned = unsaved.Count == 0 && matches.All(m => m.IsReturned);

            if (allReturned)
            {
                return new SkuWarning(
                    "Returned Item Being Sold Again",
                    $"SKU {key} was sold before and came back:\n\n• {string.Join("\n• ", matches.Select(m => m.Description))}\n\n" +
                    "Confirm this is the returned item being sold again.",
                    IsResaleOfReturnedItem: true);
            }

            var active = matches.Where(m => !m.IsReturned).Select(m => m.Description).Concat(unsaved).ToList();
            var returned = matches.Where(m => m.IsReturned).Select(m => m.Description).ToList();

            var message = $"SKU {key} is already on another sale that hasn't been returned:\n\n• {string.Join("\n• ", active)}";
            if (returned.Count > 0)
            {
                message += $"\n\nEarlier sale(s) of this SKU that were returned:\n• {string.Join("\n• ", returned)}";
            }
            message += "\n\nUsed and eBay items are one of a kind, so this may be a double entry. Add it anyway?";

            return new SkuWarning("Duplicate SKU", message, IsResaleOfReturnedItem: false);
        }
    }
}

using System.Collections.Generic;
using System.Linq;
using SalesLedger.Core.Models;

namespace SalesLedger.Core.Services
{
    /// <summary>
    /// KPI totals for a set of records (e.g. one payout report), using the same rules as the dashboard
    /// (<see cref="AnalyticsService.GetSummary(AnalyticsRange)"/>), so the PDF and the app always agree:
    /// sales are gross, returns are listed separately, units are net of full returns, commission is net.
    /// </summary>
    public sealed class PeriodTotals
    {
        public decimal GrossSales { get; private init; }
        public decimal Returns { get; private init; }
        public decimal NetSales => GrossSales - Returns;

        public int GrossUnits { get; private init; }
        public int UnitsReturned { get; private init; }
        public int NetUnits => GrossUnits - UnitsReturned;

        public decimal Commission { get; private init; }
        public decimal AverageSalePrice { get; private init; }

        /// <summary>A sale (not a return line, not a legacy return-offset record).</summary>
        public static bool IsSaleRow(SaleRecord s) => !s.IsReturn && s.RecordType != SaleType.ReturnOffset;

        /// <summary>A refund record: a return line, or a legacy return-offset record.</summary>
        public static bool IsReturnLine(SaleRecord s) => s.IsReturn || s.RecordType == SaleType.ReturnOffset;

        /// <summary>The unit came back: returned before payout, a return line completing the refund, or a legacy offset.</summary>
        public static bool IsUnitReturned(SaleRecord s) =>
            s.Status == PayoutStatus.ReturnedBeforePayout
            || (s.IsReturn && s.CompletesRefund)
            || s.RecordType == SaleType.ReturnOffset;

        public static PeriodTotals From(IEnumerable<SaleRecord> records)
        {
            var list = records.ToList();
            // ASP: every sale type, but only items that were kept (not returned before payout, not fully refunded).
            // Only refunds among these records count, so a regenerated report never changes because of a later return.
            var fullyRefunded = list.Where(s => s.IsReturn && s.CompletesRefund && s.OriginalSaleId != null)
                .Select(s => s.OriginalSaleId!.Value)
                .ToHashSet();
            var keptSales = list
                .Where(s => IsSaleRow(s) && s.Status != PayoutStatus.ReturnedBeforePayout && s.SalePrice > 0 && !fullyRefunded.Contains(s.Id))
                .ToList();

            return new PeriodTotals
            {
                GrossSales = list.Where(IsSaleRow).Sum(s => s.SalePrice),
                Returns = list.Where(IsReturnLine).Sum(s => -s.SalePrice)
                          + list.Where(s => IsSaleRow(s) && s.Status == PayoutStatus.ReturnedBeforePayout).Sum(s => s.SalePrice),
                GrossUnits = list.Count(IsSaleRow),
                UnitsReturned = list.Count(IsUnitReturned),
                Commission = list.Sum(s => s.CalculatedCommission),
                AverageSalePrice = keptSales.Count > 0 ? keptSales.Average(s => s.SalePrice) : 0m
            };
        }
    }
}

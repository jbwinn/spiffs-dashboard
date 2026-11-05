using System;
using System.Collections.Generic;
using System.Globalization;
using DuckDB.NET.Data;

namespace SalesLedger.Core.Services
{
    public class DashboardSummary
    {
        /// <summary>Everything sold in the range, including items later returned.</summary>
        public decimal GrossSales { get; init; }

        /// <summary>Refunds in the range (return lines + full returns before payout), as a positive amount.</summary>
        public decimal Returns { get; init; }

        public decimal NetSales => GrossSales - Returns;

        /// <summary>Items sold in the range, less items that came back (full returns). Partial refunds don't reduce it.</summary>
        public int TotalUnitsSold => GrossUnitsSold - UnitsReturned;

        /// <summary>Items sold in the range, including items later returned.</summary>
        public int GrossUnitsSold { get; init; }

        /// <summary>Items fully returned in the range (on the date the refund was completed).</summary>
        public int UnitsReturned { get; init; }

        /// <summary>Number of return transactions in the range (each partial return counts once).</summary>
        public int ReturnCount { get; init; }

        /// <summary>Net of clawbacks: what will actually be paid.</summary>
        public decimal TotalCommission { get; init; }

        /// <summary>Average price of items sold and kept: all sale types (new, used, eBay, warranty), excluding items
        /// returned before payout or fully refunded after payout.</summary>
        public decimal AverageSalePrice { get; init; }
    }

    /// <summary>Gross per-category totals for a bucket (returns are reported separately).</summary>
    public class CategoryMetric
    {
        public double Revenue { get; init; }
        public double Quantity { get; init; }
        public double Commission { get; init; }
    }

    public class TypeMetric
    {
        public double Revenue { get; set; }
        public double Quantity { get; set; }
        public double Commission { get; set; }
    }

    public class TrendBucket
    {
        public DateTime PeriodStart { get; init; }
        public string Label { get; init; } = string.Empty;
        public double TotalRevenue { get; set; }
        public double TotalQuantity { get; set; }
        public double TotalCommission { get; set; }
        public double MaxSalePrice { get; set; }
        public Dictionary<string, CategoryMetric> CategoryBreakdown { get; } = new();
        public TypeMetric StandardMetric { get; } = new();
        public TypeMetric EbayMetric { get; } = new();
        public TypeMetric WarrantyMetric { get; } = new();

        /// <summary>Returns in the bucket. Revenue and commission are negative.</summary>
        public TypeMetric ReturnOffsetMetric { get; } = new();
    }

    /// <summary>
    /// The window the dashboard analyses. <see cref="CurrentPayPeriodOnly"/> limits it to records the next
    /// period close would include (pending, plus returned-before-payout items not yet on a report), whatever their dates.
    /// </summary>
    public sealed record AnalyticsRange(DateTime Start, DateTime End, string Scale, bool CurrentPayPeriodOnly = false);

    public class AnalyticsService(DuckDbService duckDb)
    {
        private readonly DuckDbService _duckDb = duckDb ?? throw new ArgumentNullException(nameof(duckDb));

        public const string CurrentPayPeriod = "Current Pay Period";
        public const string Custom = "Custom";

        // Row classification shared by every query
        private const string IsSaleRow = "(IsReturn = false AND RecordType != 'ReturnOffset')";
        private const string IsReturnLineRow = "(IsReturn = true OR RecordType = 'ReturnOffset')";

        // A unit comes back when a sale is returned before payout, when a return line completes the refund,
        // or with a legacy ReturnOffset record (those were always full returns)
        private const string IsUnitReturnedRow =
            "(Status = 'ReturnedBeforePayout' OR (IsReturn = true AND COALESCE(CompletesRefund, false)) OR RecordType = 'ReturnOffset')";
        private const string CurrentPeriodFilter =
            " AND (Status = 'Pending' OR (Status = 'ReturnedBeforePayout' AND COALESCE(AssociatedReportId, '') = ''))";

        public (DateTime Start, DateTime End, string Scale) GetDateRangeAndScale(string timeframe)
        {
            var localNow = DateTime.Now;
            var today = localNow.Date;

            return timeframe.Replace(" ", "").ToLowerInvariant() switch
            {
                "currentmonth" => (new DateTime(today.Year, today.Month, 1), localNow, "day"),
                "lastmonth" => (new DateTime(today.Year, today.Month, 1).AddMonths(-1), new DateTime(today.Year, today.Month, 1).AddMilliseconds(-1), "day"),
                "last30days" => (today.AddDays(-30), localNow, "day"),
                "last3months" => (today.AddMonths(-3), localNow, "week"),
                "last6months" => (today.AddMonths(-6), localNow, "month"),
                "yeartodate" or "ytd" => (new DateTime(today.Year, 1, 1), localNow, "month"),
                _ => (today.AddDays(-30), localNow, "day")
            };
        }

        /// <summary>
        /// Turns a timeframe choice into a concrete range. "Custom" uses the given dates (inclusive, whole days);
        /// "Current Pay Period" spans from the oldest unpaid record to today.
        /// </summary>
        public AnalyticsRange ResolveRange(string timeframe, DateTime? customStart = null, DateTime? customEnd = null)
        {
            if (string.Equals(timeframe, Custom, StringComparison.OrdinalIgnoreCase))
            {
                var start = (customStart ?? DateTime.Today.AddDays(-30)).Date;
                var end = (customEnd ?? DateTime.Today).Date;
                if (end < start)
                {
                    (start, end) = (end, start);
                }
                return new AnalyticsRange(start, end.AddDays(1).AddTicks(-1), ScaleFor(start, end));
            }

            if (string.Equals(timeframe, CurrentPayPeriod, StringComparison.OrdinalIgnoreCase))
            {
                var (oldest, newest) = GetCurrentPeriodDateSpan();
                var start = (oldest ?? DateTime.Today).Date;
                var end = newest.HasValue && newest.Value > DateTime.Now ? newest.Value : DateTime.Now;
                return new AnalyticsRange(start, end, ScaleFor(start, end), CurrentPayPeriodOnly: true);
            }

            var (s, e, scale) = GetDateRangeAndScale(timeframe);
            return new AnalyticsRange(s, e, scale);
        }

        /// <summary>Day buckets up to ~6 weeks, week buckets up to ~6 months, month buckets beyond.</summary>
        internal static string ScaleFor(DateTime start, DateTime end)
        {
            var days = (end.Date - start.Date).TotalDays;
            return days <= 45 ? "day" : days <= 190 ? "week" : "month";
        }

        private (DateTime? Oldest, DateTime? Newest) GetCurrentPeriodDateSpan()
        {
            using var conn = _duckDb.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT MIN(TransactionDate), MAX(TransactionDate) FROM sales WHERE 1 = 1" + CurrentPeriodFilter + ";";
            using var reader = cmd.ExecuteReader();
            if (reader.Read() && !reader.IsDBNull(0))
            {
                return (reader.GetDateTime(0), reader.GetDateTime(1));
            }
            return (null, null);
        }

        public DashboardSummary GetSummary(string timeframe) => GetSummary(ResolveRange(timeframe));

        public DashboardSummary GetSummary(AnalyticsRange range)
        {
            using var conn = _duckDb.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                SELECT
                    COALESCE(SUM(CASE WHEN {IsSaleRow} THEN SalePrice ELSE 0.0 END), 0.0) AS GrossSales,
                    COALESCE(SUM(CASE WHEN {IsReturnLineRow} THEN -SalePrice
                                      WHEN Status = 'ReturnedBeforePayout' THEN SalePrice
                                      ELSE 0.0 END), 0.0) AS Returns,
                    COALESCE(SUM(CASE WHEN {IsSaleRow} THEN 1 ELSE 0 END), 0) AS Units,
                    COALESCE(SUM(CASE WHEN {IsReturnLineRow} OR Status = 'ReturnedBeforePayout' THEN 1 ELSE 0 END), 0) AS ReturnCount,
                    COALESCE(SUM(CalculatedCommission), 0.0) AS TotalCommission,
                    COALESCE(AVG(CASE WHEN {IsSaleRow} AND Status != 'ReturnedBeforePayout' AND SalePrice > 0
                                           AND Id NOT IN (SELECT OriginalSaleId FROM sales
                                                          WHERE IsReturn = true AND COALESCE(CompletesRefund, false) AND OriginalSaleId IS NOT NULL)
                                      THEN SalePrice ELSE NULL END), 0.0) AS ASP,
                    COALESCE(SUM(CASE WHEN {IsUnitReturnedRow} THEN 1 ELSE 0 END), 0) AS UnitsReturned
                FROM sales
                WHERE TransactionDate >= $start AND TransactionDate <= $end{PeriodFilter(range)};";

            cmd.Parameters.Add(new DuckDBParameter("start", range.Start));
            cmd.Parameters.Add(new DuckDBParameter("end", range.End));

            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                return new DashboardSummary
                {
                    GrossSales = ConvertToDecimal(reader.GetValue(0)),
                    Returns = ConvertToDecimal(reader.GetValue(1)),
                    GrossUnitsSold = ConvertToInt(reader.GetValue(2)),
                    ReturnCount = ConvertToInt(reader.GetValue(3)),
                    TotalCommission = ConvertToDecimal(reader.GetValue(4)),
                    AverageSalePrice = ConvertToDecimal(reader.GetValue(5)),
                    UnitsReturned = ConvertToInt(reader.GetValue(6))
                };
            }

            return new DashboardSummary();
        }

        public List<TrendBucket> GetTrends(string timeframe) => GetTrends(ResolveRange(timeframe));

        /// <summary>
        /// Buckets for the trend chart. Sale figures are gross (they include items later returned); returns are
        /// reported separately in <see cref="TrendBucket.ReturnOffsetMetric"/> on the date of the return, so a
        /// return is never counted twice.
        /// </summary>
        public List<TrendBucket> GetTrends(AnalyticsRange range)
        {
            var scale = range.Scale;

            using var conn = _duckDb.CreateConnection();
            using var cmd = conn.CreateCommand();

            cmd.CommandText = $@"
                SELECT
                    date_trunc('{scale}', TransactionDate) AS BucketDate,
                    COALESCE(SUM(CASE WHEN RecordType = 'Standard' AND IsReturn = false THEN SalePrice ELSE 0.0 END), 0.0) AS StandardRevenue,
                    COALESCE(SUM(CASE WHEN RecordType = 'Standard' AND IsReturn = false THEN 1 ELSE 0 END), 0) AS StandardQuantity,
                    COALESCE(SUM(CASE WHEN RecordType = 'Standard' AND IsReturn = false THEN CalculatedCommission ELSE 0.0 END), 0.0) AS StandardCommission,

                    COALESCE(SUM(CASE WHEN RecordType = 'Ebay' AND IsReturn = false THEN SalePrice ELSE 0.0 END), 0.0) AS EbayRevenue,
                    COALESCE(SUM(CASE WHEN RecordType = 'Ebay' AND IsReturn = false THEN 1 ELSE 0 END), 0) AS EbayQuantity,
                    COALESCE(SUM(CASE WHEN RecordType = 'Ebay' AND IsReturn = false THEN CalculatedCommission ELSE 0.0 END), 0.0) AS EbayCommission,

                    COALESCE(SUM(CASE WHEN RecordType = 'Warranty' AND IsReturn = false THEN SalePrice ELSE 0.0 END), 0.0) AS WarrantyRevenue,
                    COALESCE(SUM(CASE WHEN RecordType = 'Warranty' AND IsReturn = false THEN 1 ELSE 0 END), 0) AS WarrantyQuantity,
                    COALESCE(SUM(CASE WHEN RecordType = 'Warranty' AND IsReturn = false THEN CalculatedCommission ELSE 0.0 END), 0.0) AS WarrantyCommission,

                    COALESCE(SUM(CASE WHEN {IsReturnLineRow} THEN SalePrice WHEN Status = 'ReturnedBeforePayout' THEN -SalePrice ELSE 0.0 END), 0.0) AS ReturnRevenue,
                    COALESCE(SUM(CASE WHEN {IsUnitReturnedRow} THEN 1 ELSE 0 END), 0) AS ReturnQuantity,
                    COALESCE(SUM(CASE WHEN {IsReturnLineRow} THEN CalculatedCommission ELSE 0.0 END), 0.0) AS ReturnCommission,

                    COALESCE(Category, 'Uncategorized') AS CategoryName,
                    COALESCE(MAX(CASE WHEN {IsSaleRow} THEN SalePrice ELSE 0.0 END), 0.0) AS MaxSalePrice,
                    COALESCE(SUM(CASE WHEN {IsSaleRow} THEN SalePrice ELSE 0.0 END), 0.0) AS CategoryRevenue,
                    COALESCE(SUM(CASE WHEN {IsSaleRow} THEN 1 ELSE 0 END), 0) AS CategoryQuantity,
                    COALESCE(SUM(CASE WHEN {IsSaleRow} THEN CalculatedCommission ELSE 0.0 END), 0.0) AS CategoryCommission
                FROM sales
                WHERE TransactionDate >= $start AND TransactionDate <= $end{PeriodFilter(range)}
                GROUP BY BucketDate, CategoryName
                ORDER BY BucketDate ASC, CategoryName ASC;";

            cmd.Parameters.Add(new DuckDBParameter("start", range.Start));
            cmd.Parameters.Add(new DuckDBParameter("end", range.End));

            var bucketsDict = new Dictionary<DateTime, TrendBucket>();

            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    var bucketDate = reader.GetDateTime(0);
                    var stdRev = ConvertToDouble(reader.GetValue(1));
                    var stdQty = ConvertToDouble(reader.GetValue(2));
                    var stdComm = ConvertToDouble(reader.GetValue(3));

                    var ebayRev = ConvertToDouble(reader.GetValue(4));
                    var ebayQty = ConvertToDouble(reader.GetValue(5));
                    var ebayComm = ConvertToDouble(reader.GetValue(6));

                    var warRev = ConvertToDouble(reader.GetValue(7));
                    var warQty = ConvertToDouble(reader.GetValue(8));
                    var warComm = ConvertToDouble(reader.GetValue(9));

                    var retRev = ConvertToDouble(reader.GetValue(10));
                    var retQty = ConvertToDouble(reader.GetValue(11));
                    var retComm = ConvertToDouble(reader.GetValue(12));

                    var category = reader.GetString(13);
                    var maxSale = ConvertToDouble(reader.GetValue(14));
                    var catRev = ConvertToDouble(reader.GetValue(15));
                    var catQty = ConvertToDouble(reader.GetValue(16));
                    var catComm = ConvertToDouble(reader.GetValue(17));

                    if (!bucketsDict.TryGetValue(bucketDate, out var bucket))
                    {
                        bucket = new TrendBucket
                        {
                            PeriodStart = bucketDate,
                            Label = FormatLabel(bucketDate, scale)
                        };
                        bucketsDict[bucketDate] = bucket;
                    }

                    bucket.TotalRevenue += (stdRev + ebayRev + warRev);
                    bucket.TotalQuantity += (stdQty + ebayQty + warQty);
                    bucket.TotalCommission += (stdComm + ebayComm + warComm);
                    bucket.MaxSalePrice = Math.Max(bucket.MaxSalePrice, maxSale);

                    bucket.StandardMetric.Revenue += stdRev;
                    bucket.StandardMetric.Quantity += stdQty;
                    bucket.StandardMetric.Commission += stdComm;

                    bucket.EbayMetric.Revenue += ebayRev;
                    bucket.EbayMetric.Quantity += ebayQty;
                    bucket.EbayMetric.Commission += ebayComm;

                    bucket.WarrantyMetric.Revenue += warRev;
                    bucket.WarrantyMetric.Quantity += warQty;
                    bucket.WarrantyMetric.Commission += warComm;

                    bucket.ReturnOffsetMetric.Revenue += retRev;
                    bucket.ReturnOffsetMetric.Quantity += retQty;
                    bucket.ReturnOffsetMetric.Commission += retComm;

                    if (catQty > 0)
                    {
                        bucket.CategoryBreakdown[category] = new CategoryMetric
                        {
                            Revenue = catRev,
                            Quantity = catQty,
                            Commission = catComm
                        };
                    }
                }
            }

            // Fill in missing empty buckets between start and end to make graph contiguous
            var result = new List<TrendBucket>();
            var current = AlignToScale(range.Start, scale);
            var alignedEnd = AlignToScale(range.End, scale);

            while (current <= alignedEnd)
            {
                if (bucketsDict.TryGetValue(current, out var existingBucket))
                {
                    result.Add(existingBucket);
                }
                else
                {
                    result.Add(new TrendBucket
                    {
                        PeriodStart = current,
                        Label = FormatLabel(current, scale)
                    });
                }

                current = StepPeriod(current, scale);
            }

            return result;
        }

        private static string PeriodFilter(AnalyticsRange range) => range.CurrentPayPeriodOnly ? CurrentPeriodFilter : string.Empty;

        private DateTime AlignToScale(DateTime dt, string scale)
        {
            switch (scale)
            {
                case "day":
                    return dt.Date;
                case "week":
                    // Align to Monday
                    int diff = (7 + (dt.DayOfWeek - DayOfWeek.Monday)) % 7;
                    return dt.AddDays(-1 * diff).Date;
                case "month":
                    return new DateTime(dt.Year, dt.Month, 1);
                default:
                    return dt.Date;
            }
        }

        private DateTime StepPeriod(DateTime dt, string scale)
        {
            switch (scale)
            {
                case "day":
                    return dt.AddDays(1);
                case "week":
                    return dt.AddDays(7);
                case "month":
                    return dt.AddMonths(1);
                default:
                    return dt.AddDays(1);
            }
        }

        private string FormatLabel(DateTime dt, string scale)
        {
            switch (scale)
            {
                case "day":
                    return dt.ToString("MMM dd", CultureInfo.InvariantCulture);
                case "week":
                    return dt.ToString("MM/dd", CultureInfo.InvariantCulture);
                case "month":
                    return dt.ToString("MMM yyyy", CultureInfo.InvariantCulture);
                default:
                    return dt.ToShortDateString();
            }
        }

        private static int ConvertToInt(object value)
        {
            if (value is System.Numerics.BigInteger bi)
            {
                return (int)bi;
            }
            return Convert.ToInt32(value);
        }

        private static double ConvertToDouble(object value)
        {
            if (value is System.Numerics.BigInteger bi)
            {
                return (double)bi;
            }
            return Convert.ToDouble(value);
        }

        private static decimal ConvertToDecimal(object value)
        {
            if (value is System.Numerics.BigInteger bi)
            {
                return (decimal)bi;
            }
            return Convert.ToDecimal(value);
        }
    }
}

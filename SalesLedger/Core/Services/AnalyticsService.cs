using System;
using System.Collections.Generic;
using System.Globalization;
using DuckDB.NET.Data;

namespace SalesLedger.Core.Services
{
    public class DashboardSummary
    {
        public decimal TotalSales { get; init; }
        public int TotalUnitsSold { get; init; }
        public decimal TotalCommission { get; init; }
        public decimal AverageSalePrice { get; init; }
    }

    public class CategoryMetric
    {
        public double Revenue { get; init; }
        public double Quantity { get; init; }
        public double Commission { get; init; }
        public double PositiveRevenue { get; init; }
        public double PositiveQuantity { get; init; }
        public double PositiveCommission { get; init; }
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
        public TypeMetric ReturnOffsetMetric { get; } = new();
    }

    public class AnalyticsService(DuckDbService duckDb)
    {
        private readonly DuckDbService _duckDb = duckDb ?? throw new ArgumentNullException(nameof(duckDb));

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

        public DashboardSummary GetSummary(string timeframe)
        {
            var (start, end, _) = GetDateRangeAndScale(timeframe);

            using var conn = _duckDb.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT 
                    COALESCE(SUM(SalePrice), 0.0) AS TotalSales,
                    COALESCE(SUM(CASE WHEN RecordType != 'ReturnOffset' AND IsReturn = false THEN 1 ELSE 0 END), 0) AS TotalUnits,
                    COALESCE(SUM(CalculatedCommission), 0.0) AS TotalCommission,
                    COALESCE(AVG(CASE WHEN (RecordType = 'Standard' OR RecordType = 'Ebay') AND IsReturn = false AND SalePrice > 0 THEN SalePrice ELSE NULL END), 0.0) AS ASP
                FROM sales
                WHERE TransactionDate >= $start AND TransactionDate <= $end AND Status != 'ReturnedBeforePayout';";

            cmd.Parameters.Add(new DuckDBParameter("start", start));
            cmd.Parameters.Add(new DuckDBParameter("end", end));

            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                return new DashboardSummary
                {
                    TotalSales = ConvertToDecimal(reader.GetValue(0)),
                    TotalUnitsSold = ConvertToInt(reader.GetValue(1)),
                    TotalCommission = ConvertToDecimal(reader.GetValue(2)),
                    AverageSalePrice = ConvertToDecimal(reader.GetValue(3))
                };
            }

            return new DashboardSummary();
        }

        public List<TrendBucket> GetTrends(string timeframe)
        {
            var (start, end, scale) = GetDateRangeAndScale(timeframe);

            using var conn = _duckDb.CreateConnection();
            using var cmd = conn.CreateCommand();
            
            cmd.CommandText = $@"
                SELECT 
                    date_trunc('{scale}', TransactionDate) AS BucketDate,
                    COALESCE(SUM(CASE WHEN RecordType = 'Standard' AND IsReturn = false AND Status != 'ReturnedBeforePayout' THEN SalePrice ELSE 0.0 END), 0.0) AS StandardRevenue,
                    COALESCE(SUM(CASE WHEN RecordType = 'Standard' AND IsReturn = false AND Status != 'ReturnedBeforePayout' THEN 1 ELSE 0 END), 0) AS StandardQuantity,
                    COALESCE(SUM(CASE WHEN RecordType = 'Standard' AND IsReturn = false AND Status != 'ReturnedBeforePayout' THEN CalculatedCommission ELSE 0.0 END), 0.0) AS StandardCommission,

                    COALESCE(SUM(CASE WHEN RecordType = 'Ebay' AND IsReturn = false AND Status != 'ReturnedBeforePayout' THEN SalePrice ELSE 0.0 END), 0.0) AS EbayRevenue,
                    COALESCE(SUM(CASE WHEN RecordType = 'Ebay' AND IsReturn = false AND Status != 'ReturnedBeforePayout' THEN 1 ELSE 0 END), 0) AS EbayQuantity,
                    COALESCE(SUM(CASE WHEN RecordType = 'Ebay' AND IsReturn = false AND Status != 'ReturnedBeforePayout' THEN CalculatedCommission ELSE 0.0 END), 0.0) AS EbayCommission,

                    COALESCE(SUM(CASE WHEN RecordType = 'Warranty' AND IsReturn = false AND Status != 'ReturnedBeforePayout' THEN SalePrice ELSE 0.0 END), 0.0) AS WarrantyRevenue,
                    COALESCE(SUM(CASE WHEN RecordType = 'Warranty' AND IsReturn = false AND Status != 'ReturnedBeforePayout' THEN 1 ELSE 0 END), 0) AS WarrantyQuantity,
                    COALESCE(SUM(CASE WHEN RecordType = 'Warranty' AND IsReturn = false AND Status != 'ReturnedBeforePayout' THEN CalculatedCommission ELSE 0.0 END), 0.0) AS WarrantyCommission,

                    COALESCE(SUM(CASE WHEN IsReturn = true THEN SalePrice WHEN Status = 'ReturnedBeforePayout' THEN -SalePrice ELSE 0.0 END), 0.0) AS ReturnRevenue,
                    COALESCE(SUM(CASE WHEN IsReturn = true THEN 1 WHEN Status = 'ReturnedBeforePayout' THEN 1 ELSE 0 END), 0) AS ReturnQuantity,
                    COALESCE(SUM(CASE WHEN IsReturn = true THEN CalculatedCommission WHEN Status = 'ReturnedBeforePayout' THEN 0.0 ELSE 0.0 END), 0.0) AS ReturnCommission,

                    COALESCE(Category, 'Uncategorized') AS CategoryName,
                    COALESCE(SUM(CASE WHEN Status = 'ReturnedBeforePayout' THEN -SalePrice ELSE SalePrice END), 0.0) AS Revenue,
                    COALESCE(SUM(CASE WHEN RecordType = 'ReturnOffset' OR IsReturn = true OR Status = 'ReturnedBeforePayout' THEN -1 ELSE 1 END), 0) AS Quantity,
                    COALESCE(SUM(CASE WHEN Status = 'ReturnedBeforePayout' THEN -CalculatedCommission ELSE CalculatedCommission END), 0.0) AS Commission,
                    COALESCE(MAX(CASE WHEN IsReturn = false AND Status != 'ReturnedBeforePayout' THEN SalePrice ELSE 0.0 END), 0.0) AS MaxSalePrice,
                    COALESCE(SUM(CASE WHEN RecordType != 'ReturnOffset' AND IsReturn = false AND Status != 'ReturnedBeforePayout' THEN SalePrice ELSE 0.0 END), 0.0) AS PositiveRevenue,
                    COALESCE(SUM(CASE WHEN RecordType != 'ReturnOffset' AND IsReturn = false AND Status != 'ReturnedBeforePayout' THEN 1 ELSE 0 END), 0) AS PositiveQuantity,
                    COALESCE(SUM(CASE WHEN RecordType != 'ReturnOffset' AND IsReturn = false AND Status != 'ReturnedBeforePayout' THEN CalculatedCommission ELSE 0.0 END), 0.0) AS PositiveCommission
                FROM sales
                WHERE TransactionDate >= $start AND TransactionDate <= $end
                GROUP BY BucketDate, CategoryName
                ORDER BY BucketDate ASC, CategoryName ASC;";

            cmd.Parameters.Add(new DuckDBParameter("start", start));
            cmd.Parameters.Add(new DuckDBParameter("end", end));

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
                    var revenue = ConvertToDouble(reader.GetValue(14));
                    var quantity = ConvertToDouble(reader.GetValue(15));
                    var commission = ConvertToDouble(reader.GetValue(16));
                    var maxSale = ConvertToDouble(reader.GetValue(17));
                    var posRev = ConvertToDouble(reader.GetValue(18));
                    var posQty = ConvertToDouble(reader.GetValue(19));
                    var posComm = ConvertToDouble(reader.GetValue(20));

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

                    bucket.CategoryBreakdown[category] = new CategoryMetric
                    {
                        Revenue = revenue,
                        Quantity = quantity,
                        Commission = commission,
                        PositiveRevenue = posRev,
                        PositiveQuantity = posQty,
                        PositiveCommission = posComm
                    };
                }
            }

            // Fill in missing empty buckets between start and end to make graph contiguous
            var result = new List<TrendBucket>();
            var current = AlignToScale(start, scale);
            var alignedEnd = AlignToScale(end, scale);

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

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using SalesLedger.Core.Models;

namespace SalesLedger.Core.Services
{
    /// <summary>Which CSV header feeds each field. Null means the column isn't in the file.</summary>
    public sealed class CsvColumnMapping
    {
        public string? Date { get; init; }
        public string? Invoice { get; init; }
        public string? Sku { get; init; }
        public string? ProductName { get; init; }
        public string? Category { get; init; }
        public string? Price { get; init; }
        public string? IsUsed { get; init; }
        public string? WarrantyType { get; init; }
        public string? WholesalePrice { get; init; }
    }

    public sealed class CsvImportOptions
    {
        /// <summary>Standard, Warranty or Ebay.</summary>
        public SaleType ImportType { get; init; } = SaleType.Standard;
        public string DefaultCategory { get; init; } = string.Empty;
        public string DefaultWarrantyType { get; init; } = string.Empty;

        /// <summary>Used-gear value for rows whose used column is blank or unrecognized.</summary>
        public bool TreatUnmarkedAsUsed { get; init; } = true;

        /// <summary>Used for rows without a date, and to build invoice numbers for rows without one.</summary>
        public DateTime ImportTime { get; init; } = DateTime.Now;

        /// <summary>When the file has a date column, a blank date is an error rather than "today".</summary>
        public bool RequireDate { get; init; }

        /// <summary>When the file has a wholesale column, a blank wholesale price is an error rather than $0.</summary>
        public bool RequireWholesalePrice { get; init; }
    }

    /// <summary>One data row of the file, as editable text. Edits made during review are written back here.</summary>
    public sealed class CsvImportRow
    {
        /// <summary>Spreadsheet-style row number (header is row 1).</summary>
        public int RowNumber { get; init; }
        public string[] RawCells { get; init; } = [];

        public string Date { get; set; } = string.Empty;
        public string Invoice { get; set; } = string.Empty;
        public string Sku { get; set; } = string.Empty;
        public string ProductName { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string Price { get; set; } = string.Empty;
        public string IsUsed { get; set; } = string.Empty;
        public string WarrantyType { get; set; } = string.Empty;
        public string WholesalePrice { get; set; } = string.Empty;
    }

    public sealed record CsvSkippedRow(int RowNumber, string Reason);

    public sealed class CsvParseResult
    {
        public List<string> Headers { get; init; } = [];
        public List<CsvImportRow> Rows { get; init; } = [];
        public List<CsvSkippedRow> Skipped { get; init; } = [];

        /// <summary>File-level notes, e.g. a column that isn't mapped and what will be assumed instead.</summary>
        public List<string> Warnings { get; init; } = [];
    }

    public sealed class CsvRowValidation
    {
        /// <summary>The sale this row would create, or null if the row has errors.</summary>
        public SaleRecord? Record { get; init; }
        public List<string> Errors { get; init; } = [];

        /// <summary>True when the file had no invoice number for this row and one was generated.</summary>
        public bool InvoiceGenerated { get; init; }
        public bool IsValid => Record != null;
    }

    /// <summary>
    /// CSV import with a review step. Nothing is written until <see cref="Import"/> is called:
    /// rows are parsed, validated (bad values are reported, never guessed), checked for possible
    /// duplicates against the ledger, and then imported together in one transaction.
    /// </summary>
    public class CsvImportService(LiteDbService db, SyncPipeline sync, CommissionProcessor processor)
    {
        public const string ProblemColumnHeader = "Import Problem";

        private static readonly Regex TotalsCell = new(@"^(grand\s*|sub\s*)?totals?:?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public CsvParseResult Parse(List<string[]> csvRows, CsvColumnMapping mapping, CsvImportOptions options)
        {
            var result = new CsvParseResult();
            if (csvRows.Count == 0)
            {
                return result;
            }

            result.Headers.AddRange(csvRows[0]);
            var headers = result.Headers;

            int Index(string? header) => header == null ? -1 : headers.IndexOf(header);
            int dateIdx = Index(mapping.Date), invIdx = Index(mapping.Invoice), skuIdx = Index(mapping.Sku),
                nameIdx = Index(mapping.ProductName), catIdx = Index(mapping.Category), priceIdx = Index(mapping.Price),
                usedIdx = Index(mapping.IsUsed), warIdx = Index(mapping.WarrantyType), wholesaleIdx = Index(mapping.WholesalePrice);

            if (dateIdx < 0)
            {
                result.Warnings.Add($"No date column is mapped. Rows without a date will use today's date ({options.ImportTime:d}).");
            }
            if (invIdx < 0)
            {
                result.Warnings.Add("No invoice column is mapped. Each row will get its own generated invoice number.");
            }
            if (options.ImportType == SaleType.Warranty && wholesaleIdx < 0)
            {
                result.Warnings.Add("No wholesale price column is mapped. Rows without a wholesale price will use $0, so warranty commission will be based on the full sale price.");
            }

            for (int i = 1; i < csvRows.Count; i++)
            {
                var cells = csvRows[i];
                string Get(int idx) => idx >= 0 && idx < cells.Length ? cells[idx].Trim() : string.Empty;

                var row = new CsvImportRow
                {
                    RowNumber = i + 1,
                    RawCells = cells,
                    Date = Get(dateIdx),
                    Invoice = Get(invIdx),
                    Sku = Get(skuIdx),
                    ProductName = Get(nameIdx),
                    Category = Get(catIdx),
                    Price = Get(priceIdx),
                    IsUsed = Get(usedIdx),
                    WarrantyType = Get(warIdx),
                    WholesalePrice = Get(wholesaleIdx)
                };

                if (TotalsCell.IsMatch(row.Date) || TotalsCell.IsMatch(row.Sku) || TotalsCell.IsMatch(row.ProductName))
                {
                    result.Skipped.Add(new CsvSkippedRow(row.RowNumber, "Looks like a totals row"));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(row.Sku) && string.IsNullOrWhiteSpace(row.ProductName))
                {
                    result.Skipped.Add(new CsvSkippedRow(row.RowNumber, "No SKU or product name"));
                    continue;
                }

                result.Rows.Add(row);
            }

            return result;
        }

        /// <summary>
        /// Builds the sale a row would create, or lists what's wrong with it. Blank values fall back
        /// to the defaults chosen in the import options; values that can't be understood are errors.
        /// </summary>
        public CsvRowValidation Validate(CsvImportRow row, CsvImportOptions options, UserSettings settings)
        {
            var errors = new List<string>();

            // Date
            DateTime date = options.ImportTime;
            if (!string.IsNullOrWhiteSpace(row.Date))
            {
                if (!DateTime.TryParse(row.Date, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out date))
                {
                    errors.Add($"Can't read date '{row.Date}'");
                }
            }
            else if (options.RequireDate)
            {
                errors.Add("Date is missing");
            }

            // Price
            decimal price = 0m;
            if (string.IsNullOrWhiteSpace(row.Price))
            {
                errors.Add("Price is missing");
            }
            else if (!TryParseMoney(row.Price, out price))
            {
                errors.Add($"Can't read price '{row.Price}'");
            }
            else if (price < 0)
            {
                errors.Add($"Price can't be negative ({row.Price})");
            }

            // Type-specific fields
            SaleRecord record;
            string category;
            if (options.ImportType == SaleType.Warranty)
            {
                category = "Warranty";

                var warrantyName = string.IsNullOrWhiteSpace(row.WarrantyType) ? options.DefaultWarrantyType : row.WarrantyType;
                var warranty = (settings.WarrantyTypes ?? []).FirstOrDefault(w => w.Name.Equals(warrantyName.Trim(), StringComparison.OrdinalIgnoreCase));
                if (warranty == null)
                {
                    errors.Add(string.IsNullOrWhiteSpace(warrantyName) ? "Warranty type is missing" : $"Unknown warranty type '{warrantyName}'");
                }

                decimal wholesale = 0m;
                if (!string.IsNullOrWhiteSpace(row.WholesalePrice))
                {
                    if (!TryParseMoney(row.WholesalePrice, out wholesale))
                    {
                        errors.Add($"Can't read wholesale price '{row.WholesalePrice}'");
                    }
                    else if (wholesale < 0)
                    {
                        errors.Add($"Wholesale price can't be negative ({row.WholesalePrice})");
                    }
                }
                else if (options.RequireWholesalePrice)
                {
                    errors.Add("Wholesale price is missing (enter 0 if there is none)");
                }

                record = new WarrantySale
                {
                    WarrantyTypeName = warranty?.Name ?? string.Empty,
                    ManufacturerPrice = wholesale
                };
            }
            else
            {
                var categoryName = string.IsNullOrWhiteSpace(row.Category) ? options.DefaultCategory : row.Category;
                var match = (settings.ProductCategories ?? []).FirstOrDefault(c => c.Name.Equals(categoryName.Trim(), StringComparison.OrdinalIgnoreCase));
                if (match == null)
                {
                    errors.Add(string.IsNullOrWhiteSpace(categoryName) ? "Category is missing" : $"Unknown category '{categoryName}'");
                }
                category = match?.Name ?? string.Empty;

                bool isUsed = ParseUsed(row.IsUsed, options.TreatUnmarkedAsUsed);
                record = options.ImportType == SaleType.Ebay
                    ? new EbaySale { IsUsedGear = isUsed }
                    : new StandardSale { IsUsedGear = isUsed };
            }

            if (errors.Count > 0)
            {
                return new CsvRowValidation { Errors = errors };
            }

            bool invoiceGenerated = string.IsNullOrWhiteSpace(row.Invoice);
            record.InvoiceNumber = invoiceGenerated
                ? $"CSV-{options.ImportTime:yyyyMMdd-HHmmss}-R{row.RowNumber}"
                : row.Invoice.Trim();
            record.Sku = row.Sku.Trim();
            record.ProductName = row.ProductName.Trim();
            record.Category = category;
            record.SalePrice = price;
            record.TransactionDate = date;
            record.Status = PayoutStatus.Pending;

            return new CsvRowValidation { Record = record, InvoiceGenerated = invoiceGenerated };
        }

        /// <summary>
        /// Finds an existing sale that looks like the same transaction: same day, price and SKU, plus the
        /// same invoice number (or the same product name when the file had no invoice number).
        /// </summary>
        public SaleRecord? FindPossibleDuplicate(SaleRecord candidate, bool invoiceGenerated)
        {
            var dayStart = candidate.TransactionDate.Date;
            var dayEnd = dayStart.AddDays(1);
            var price = candidate.SalePrice;

            return db.Sales
                .Find(x => x.TransactionDate >= dayStart && x.TransactionDate < dayEnd && x.SalePrice == price && !x.IsReturn)
                .FirstOrDefault(x =>
                    string.Equals(x.Sku.Trim(), candidate.Sku.Trim(), StringComparison.OrdinalIgnoreCase) &&
                    (invoiceGenerated
                        ? string.Equals(x.ProductName.Trim(), candidate.ProductName.Trim(), StringComparison.OrdinalIgnoreCase)
                        : string.Equals(x.InvoiceNumber.Trim(), candidate.InvoiceNumber.Trim(), StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>
        /// Calculates commission and inserts all records in a single transaction (all or nothing),
        /// then mirrors them to the analytics store. Returns the number imported.
        /// </summary>
        public int Import(IReadOnlyList<SaleRecord> records)
        {
            if (records.Count == 0)
            {
                return 0;
            }

            var rules = db.GetUserSettings().ActiveRules ?? [];
            foreach (var record in records)
            {
                processor.ApplyCommission(record, rules);
            }

            db.RunInTransaction(() =>
            {
                foreach (var record in records)
                {
                    db.Sales.Insert(record);
                }
            });

            foreach (var record in records)
            {
                sync.QueueUpsert(record);
            }

            return records.Count;
        }

        /// <summary>
        /// Writes rows back out as a CSV with the original headers plus an "Import Problem" column, so they can be
        /// fixed in a spreadsheet and imported on their own. Mapped columns carry any edits made during review.
        /// </summary>
        public static void WriteRows(string filePath, IReadOnlyList<string> headers, CsvColumnMapping mapping, IEnumerable<(CsvImportRow Row, string Problem)> rows)
        {
            var output = new List<IReadOnlyList<string>> { headers.Append(ProblemColumnHeader).ToList() };

            foreach (var (row, problem) in rows)
            {
                var cells = new string[headers.Count];
                for (int i = 0; i < cells.Length; i++)
                {
                    cells[i] = i < row.RawCells.Length ? row.RawCells[i] : string.Empty;
                }

                void Set(string? header, string value)
                {
                    int idx = header == null ? -1 : IndexOf(headers, header);
                    if (idx >= 0) cells[idx] = value;
                }

                Set(mapping.Date, row.Date);
                Set(mapping.Invoice, row.Invoice);
                Set(mapping.Sku, row.Sku);
                Set(mapping.ProductName, row.ProductName);
                Set(mapping.Category, row.Category);
                Set(mapping.Price, row.Price);
                Set(mapping.IsUsed, row.IsUsed);
                Set(mapping.WarrantyType, row.WarrantyType);
                Set(mapping.WholesalePrice, row.WholesalePrice);

                output.Add(cells.Append(problem).ToList());
            }

            CsvHelper.WriteCsv(filePath, output);
        }

        /// <summary>
        /// "used", "yes", "true", "1" → used; "new", "unused", "no", "false", "0" → new; anything else → the default.
        /// </summary>
        public static bool ParseUsed(string value, bool defaultValue)
        {
            var v = value.Trim().ToLowerInvariant();
            if (v.Length == 0) return defaultValue;
            if (v.Contains("unused") || v.Contains("new") || v is "no" or "n" or "false" or "0") return false;
            if (v.Contains("used") || v is "yes" or "y" or "true" or "1") return true;
            return defaultValue;
        }

        private static bool TryParseMoney(string text, out decimal value)
        {
            var cleaned = text.Replace("$", "").Replace(",", "").Trim();
            bool negative = cleaned.StartsWith('(') && cleaned.EndsWith(')');
            if (negative)
            {
                cleaned = cleaned[1..^1];
            }

            if (decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out value))
            {
                if (negative) value = -value;
                return true;
            }

            return false;
        }

        private static int IndexOf(IReadOnlyList<string> headers, string header)
        {
            for (int i = 0; i < headers.Count; i++)
            {
                if (headers[i] == header) return i;
            }
            return -1;
        }
    }
}

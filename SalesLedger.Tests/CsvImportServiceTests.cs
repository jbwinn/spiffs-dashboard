using SalesLedger.Core.Models;
using SalesLedger.Core.Services;

namespace SalesLedger.Tests
{
    /// <summary>CSV import: bad values are reported (never guessed), duplicates are flagged, imports are all-or-nothing.</summary>
    public class CsvImportServiceTests : LedgerTestBase
    {
        private static readonly string[] Headers = ["Date", "Receipt", "SKU", "Product Name", "Category", "Price", "Condition"];

        private static readonly CsvColumnMapping Mapping = new()
        {
            Date = "Date", Invoice = "Receipt", Sku = "SKU", ProductName = "Product Name",
            Category = "Category", Price = "Price", IsUsed = "Condition"
        };

        private static CsvImportOptions Options(bool requireDate = true) => new()
        {
            ImportType = SaleType.Standard,
            DefaultCategory = "Lens",
            DefaultWarrantyType = "Sony",
            TreatUnmarkedAsUsed = true,
            ImportTime = new DateTime(2026, 11, 3, 9, 0, 0),
            RequireDate = requireDate
        };

        private List<CsvImportRow> ParseRows(params string[][] rows) =>
            CsvImport.Parse([Headers, .. rows], Mapping, Options()).Rows;

        private CsvRowValidation Validate(CsvImportRow row, CsvImportOptions? options = null) =>
            CsvImport.Validate(row, options ?? Options(), Lite.GetUserSettings());

        [Fact]
        public void Parse_IgnoresTotalsRowsAndRowsWithoutAProduct_AndSaysSo()
        {
            var result = CsvImport.Parse(
            [
                Headers,
                ["10/01/2026", "R1", "SKU1", "Lens A", "Lens", "100", ""],
                ["", "", "", "", "", "500", ""],
                ["", "", "", "Total", "", "600", ""],
                ["10/02/2026", "R2", "SKU2", "TotalCare Plan", "Lens", "50", ""]
            ], Mapping, Options());

            Assert.Equal([2, 5], result.Rows.Select(r => r.RowNumber));
            Assert.Equal(2, result.Skipped.Count);
            Assert.Contains(result.Skipped, s => s.RowNumber == 4 && s.Reason.Contains("totals"));
        }

        [Fact]
        public void Parse_WarnsAboutUnmappedColumns()
        {
            var result = CsvImport.Parse([Headers], new CsvColumnMapping { Price = "Price", Sku = "SKU" }, Options());

            Assert.Contains(result.Warnings, w => w.Contains("date"));
            Assert.Contains(result.Warnings, w => w.Contains("invoice"));
        }

        [Theory]
        [InlineData("13/45/2026", "100", "Lens", "Can't read date")]
        [InlineData("", "100", "Lens", "Date is missing")]
        [InlineData("10/01/2026", "abc", "Lens", "Can't read price")]
        [InlineData("10/01/2026", "", "Lens", "Price is missing")]
        [InlineData("10/01/2026", "-5", "Lens", "can't be negative")]
        [InlineData("10/01/2026", "100", "Lenses", "Unknown category 'Lenses'")]
        public void Validate_ReportsBadValues_InsteadOfGuessing(string date, string price, string category, string expectedError)
        {
            var row = ParseRows(["", "R1", "SKU1", "Lens A", "", "", ""])[0];
            row.Date = date;
            row.Price = price;
            row.Category = category;

            var result = Validate(row);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Contains(expectedError));
        }

        [Fact]
        public void Validate_FixedRow_BecomesValid()
        {
            var row = ParseRows(["13/45/2026", "R1", "SKU1", "Lens A", "Lenses", "$1,299.00", "Used"])[0];
            Assert.False(Validate(row).IsValid);

            row.Date = "10/01/2026";
            row.Category = "lens";
            var result = Validate(row);

            Assert.True(result.IsValid);
            var sale = Assert.IsType<StandardSale>(result.Record);
            Assert.Equal("Lens", sale.Category);
            Assert.Equal(1299.00m, sale.SalePrice);
            Assert.True(sale.IsUsedGear);
            Assert.Equal(new DateTime(2026, 10, 1), sale.TransactionDate);
        }

        [Fact]
        public void Validate_BlankCategoryUsesTheChosenDefault_AndUnmappedDateUsesImportTime()
        {
            var row = ParseRows(["", "R1", "SKU1", "Lens A", "", "100", ""])[0];

            var result = Validate(row, Options(requireDate: false));

            Assert.True(result.IsValid);
            Assert.Equal("Lens", result.Record!.Category);
            Assert.Equal(new DateTime(2026, 11, 3, 9, 0, 0), result.Record.TransactionDate);
        }

        [Fact]
        public void Validate_GeneratesAnInvoiceNumber_WhenTheRowHasNone()
        {
            var row = ParseRows(["10/01/2026", "", "SKU1", "Lens A", "Lens", "100", ""])[0];

            var result = Validate(row);

            Assert.True(result.InvoiceGenerated);
            Assert.StartsWith("CSV-20261103-090000-R2", result.Record!.InvoiceNumber);
        }

        [Fact]
        public void Validate_Warranty_RequiresKnownTypeAndWholesalePrice()
        {
            var options = new CsvImportOptions
            {
                ImportType = SaleType.Warranty, DefaultWarrantyType = "Sony", RequireDate = false, RequireWholesalePrice = true
            };
            var row = new CsvImportRow { RowNumber = 2, Sku = "W1", ProductName = "Plan", Price = "300", WarrantyType = "Sonny" };

            var result = Validate(row, options);
            Assert.Contains(result.Errors, e => e.Contains("Unknown warranty type 'Sonny'"));
            Assert.Contains(result.Errors, e => e.Contains("Wholesale price is missing"));

            row.WarrantyType = "sony";
            row.WholesalePrice = "120";
            var fixedResult = Validate(row, options);
            var sale = Assert.IsType<WarrantySale>(fixedResult.Record);
            Assert.Equal("Sony", sale.WarrantyTypeName);
            Assert.Equal(120m, sale.ManufacturerPrice);
            Assert.Equal("Warranty", sale.Category);
        }

        [Theory]
        [InlineData("Used", true, true)]
        [InlineData("used", false, true)]
        [InlineData("New", true, false)]
        [InlineData("Unused", true, false)]
        [InlineData("no", true, false)]
        [InlineData("", true, true)]
        [InlineData("", false, false)]
        [InlineData("Excellent", true, true)]
        public void ParseUsed_HonorsNewMarkers_EvenWhenDefaultIsUsed(string value, bool defaultValue, bool expected)
        {
            Assert.Equal(expected, CsvImportService.ParseUsed(value, defaultValue));
        }

        [Fact]
        public void FindPossibleDuplicate_MatchesSameDayPriceSkuAndInvoice()
        {
            AddSale(new StandardSale
            {
                InvoiceNumber = "R1", Sku = "SKU1", ProductName = "Lens A", Category = "Lens",
                SalePrice = 100m, TransactionDate = new DateTime(2026, 10, 1, 14, 30, 0)
            });

            var same = Validate(ParseRows(["10/01/2026", "R1", "sku1", "Lens A", "Lens", "100", ""])[0]);
            var otherPrice = Validate(ParseRows(["10/01/2026", "R1", "SKU1", "Lens A", "Lens", "101", ""])[0]);
            var otherDay = Validate(ParseRows(["10/02/2026", "R1", "SKU1", "Lens A", "Lens", "100", ""])[0]);
            var otherInvoice = Validate(ParseRows(["10/01/2026", "R9", "SKU1", "Lens A", "Lens", "100", ""])[0]);

            Assert.NotNull(CsvImport.FindPossibleDuplicate(same.Record!, same.InvoiceGenerated));
            Assert.Null(CsvImport.FindPossibleDuplicate(otherPrice.Record!, otherPrice.InvoiceGenerated));
            Assert.Null(CsvImport.FindPossibleDuplicate(otherDay.Record!, otherDay.InvoiceGenerated));
            Assert.Null(CsvImport.FindPossibleDuplicate(otherInvoice.Record!, otherInvoice.InvoiceGenerated));
        }

        [Fact]
        public void FindPossibleDuplicate_WithoutInvoice_MatchesOnProductName()
        {
            AddSale(new StandardSale
            {
                InvoiceNumber = "CSV-OLD-R2", Sku = "SKU1", ProductName = "Lens A", Category = "Lens",
                SalePrice = 100m, TransactionDate = new DateTime(2026, 10, 1)
            });

            var row = Validate(ParseRows(["10/01/2026", "", "SKU1", "Lens A", "Lens", "100", ""])[0]);

            Assert.True(row.InvoiceGenerated);
            Assert.NotNull(CsvImport.FindPossibleDuplicate(row.Record!, row.InvoiceGenerated));
        }

        [Fact]
        public void Import_CalculatesCommission_AndSavesEveryRecord()
        {
            var records = ParseRows(
                    ["10/01/2026", "R1", "SKU1", "Lens A", "Lens", "1000", "New"],
                    ["10/01/2026", "R1", "SKU2", "Body B", "Mirrorless", "2000", "New"])
                .Select(r => Validate(r).Record!)
                .ToList();

            Assert.Equal(2, CsvImport.Import(records));

            var saved = Lite.Sales.FindAll().OrderBy(s => s.SalePrice).ToList();
            Assert.Equal([80.00m, 100.00m], saved.Select(s => s.CalculatedCommission));
            Assert.All(saved, s => Assert.Equal(PayoutStatus.Pending, s.Status));
        }

        [Fact]
        public void Import_IsAllOrNothing()
        {
            var good = Validate(ParseRows(["10/01/2026", "R1", "SKU1", "Lens A", "Lens", "1000", ""])[0]).Record!;
            var clash = Validate(ParseRows(["10/01/2026", "R2", "SKU2", "Lens B", "Lens", "500", ""])[0]).Record!;
            clash.Id = good.Id; // forces a duplicate-key failure on the second insert

            Assert.ThrowsAny<Exception>(() => CsvImport.Import([good, clash]));

            Assert.Empty(Lite.Sales.FindAll());
        }

        [Fact]
        public void WriteRows_ProducesAFileThatCanBeFixedAndReimported()
        {
            var rows = ParseRows(["13/45/2026", "R1", "SKU1", "Lens, 50mm \"fast\"", "Lenses", "100", "Used"]);
            var row = rows[0];
            row.Category = "Lens"; // an edit made during review
            var path = Path.Combine(Path.GetTempPath(), $"problems_{Guid.NewGuid():N}.csv");

            try
            {
                CsvImportService.WriteRows(path, Headers, Mapping, [(row, "Can't read date '13/45/2026'")]);

                var written = CsvHelper.ParseCsv(path);
                Assert.Equal([.. Headers, CsvImportService.ProblemColumnHeader], written[0]);
                Assert.Equal("13/45/2026", written[1][0]);
                Assert.Equal("Lens, 50mm \"fast\"", written[1][3]);
                Assert.Equal("Lens", written[1][4]);
                Assert.Equal("Can't read date '13/45/2026'", written[1][7]);

                // The extra column doesn't get in the way of importing the fixed file
                var reparsed = CsvImport.Parse(written, Mapping, Options());
                Assert.Single(reparsed.Rows);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}

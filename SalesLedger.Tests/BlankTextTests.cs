using LiteDB;
using SalesLedger.Core.Models;
using SalesLedger.Core.Services;

namespace SalesLedger.Tests
{
    /// <summary>Blank text (e.g. a sale with no SKU) must come back as "", never null.</summary>
    public class BlankTextTests : LedgerTestBase
    {
        [Fact]
        public void BlankSku_RoundTripsAsEmptyString()
        {
            var sale = AddSale(new WarrantySale { InvoiceNumber = "INV-W", ProductName = "Plan", Category = "Warranty", SalePrice = 100m, Sku = "" });

            var loaded = Reload(sale);

            Assert.Equal(string.Empty, loaded.Sku);
        }

        [Fact]
        public void RecordsSavedTheOldWay_WithNullText_StillLoadAsEmptyStrings()
        {
            var path = Path.Combine(Path.GetTempPath(), $"legacy_{Guid.NewGuid():N}.db");
            var id = Guid.NewGuid();
            try
            {
                // Write a sale the way older versions did: LiteDB's default mapper stores blank text as null
                using (var legacy = new LiteDatabase(path, new BsonMapper()))
                {
                    legacy.GetCollection<SaleRecord>("sales").Insert(new StandardSale { Id = id, ProductName = "Old Record", SalePrice = 50m });
                }

                using var current = new LiteDbService(path);
                var loaded = current.Sales.FindById(id);

                Assert.Equal(string.Empty, loaded.Sku);
                Assert.Equal(string.Empty, loaded.InvoiceNumber);
                Assert.Equal(string.Empty, loaded.Category);
                Assert.Equal("Old Record", loaded.ProductName);
            }
            finally
            {
                try { File.Delete(path); } catch (Exception) { /* ignore */ }
            }
        }

        [Fact]
        public void SkuCheck_AndSuggestions_CopeWithBlankSkus()
        {
            AddSale(new StandardSale { ProductName = "No Sku Lens", Category = "Lens", SalePrice = 100m, IsUsedGear = true, Sku = "" });
            var skuCheck = new SkuCheckService(Lite, Payout);

            Assert.Empty(skuCheck.FindPriorSales("U-1"));
            Assert.Contains(Receipts.GetProductSuggestions(), s => s.Name == "No Sku Lens");
        }
    }
}

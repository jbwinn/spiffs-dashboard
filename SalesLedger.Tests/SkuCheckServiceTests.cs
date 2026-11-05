using SalesLedger.Core.Models;
using SalesLedger.Core.Services;

namespace SalesLedger.Tests
{
    /// <summary>
    /// Duplicate SKUs on used/eBay entries: a returned item being sold again gets a resale notice; anything else
    /// gets a duplicate warning showing the other sale. Both are confirmations, never blocks.
    /// </summary>
    public class SkuCheckServiceTests : LedgerTestBase
    {
        private readonly SkuCheckService _skuCheck;

        public SkuCheckServiceTests()
        {
            _skuCheck = new SkuCheckService(Lite, Payout);
        }

        private StandardSale Sale(string sku, bool used, PayoutStatus status = PayoutStatus.Pending, string invoice = "INV-A") =>
            AddSale(new StandardSale { InvoiceNumber = invoice, Sku = sku, ProductName = "Body " + sku, Category = "Mirrorless", SalePrice = 500m, IsUsedGear = used }, status);

        [Theory]
        [InlineData(SaleType.Standard, true, true)]    // used gear
        [InlineData(SaleType.Ebay, true, true)]
        [InlineData(SaleType.Ebay, false, true)]       // eBay is always checked
        [InlineData(SaleType.Standard, false, false)]  // new gear shares SKUs legitimately
        [InlineData(SaleType.Warranty, false, false)]
        public void OnlyUsedAndEbayEntriesAreChecked(SaleType type, bool used, bool expected)
        {
            Assert.Equal(expected, SkuCheckService.ShouldCheck(type, used));
        }

        [Fact]
        public void MatchesAnyEarlierSaleOfTheSku_EvenIfItWasEnteredAsNewGear()
        {
            // The reported bug: the earlier sale wasn't marked used, so it was never matched
            Sale("SONY-A7IV", used: false);

            var matches = _skuCheck.FindPriorSales(" sony-a7iv ");

            var match = Assert.Single(matches);
            Assert.False(match.IsReturned);
        }

        [Fact]
        public void PendingDuplicateOnAnotherInvoice_GivesADuplicateWarningShowingTheOtherSale()
        {
            Sale("U-100", used: true, invoice: "INV-A");

            var warning = SkuCheckService.BuildWarning("U-100", _skuCheck.FindPriorSales("U-100"));

            Assert.NotNull(warning);
            Assert.False(warning.IsResaleOfReturnedItem);
            Assert.Equal("Duplicate SKU", warning.Title);
            Assert.Contains("INV-A", warning.Message);
            Assert.Contains("not returned", warning.Message);
        }

        [Fact]
        public void ReturnedBeforePayout_GivesAResaleNotice()
        {
            var sale = Sale("U-RBP", used: true);
            Payout.ProcessReturn(sale.Id);

            var warning = SkuCheckService.BuildWarning("U-RBP", _skuCheck.FindPriorSales("U-RBP"));

            Assert.NotNull(warning);
            Assert.True(warning.IsResaleOfReturnedItem);
            Assert.Contains("Returned before payout", warning.Message);
        }

        [Fact]
        public void ReturnedAfterPayout_GivesAResaleNoticeWithTheReturnDate()
        {
            var sale = Sale("U-PAID", used: true, PayoutStatus.Paid);
            Payout.ProcessReturn(sale, 500m, new DateTime(2026, 10, 20, 12, 0, 0));

            var match = Assert.Single(_skuCheck.FindPriorSales("U-PAID"));
            var warning = SkuCheckService.BuildWarning("U-PAID", [match]);

            Assert.True(match.IsReturned);
            Assert.Contains("returned Oct 20, 2026", match.Description);
            Assert.True(warning!.IsResaleOfReturnedItem);
        }

        [Fact]
        public void PartialRefund_MeansTheCustomerKeptIt_SoItsADuplicate()
        {
            var sale = Sale("U-PART", used: true, PayoutStatus.Paid);
            Payout.ProcessReturn(sale, 100m);

            var match = Assert.Single(_skuCheck.FindPriorSales("U-PART"));

            Assert.False(match.IsReturned);
            Assert.Contains("partially refunded", match.Description);
            Assert.False(SkuCheckService.BuildWarning("U-PART", [match])!.IsResaleOfReturnedItem);
        }

        [Fact]
        public void ReturnedOnceAndSoldAgain_ThenEnteredAThirdTime_IsADuplicate()
        {
            var first = Sale("U-3X", used: true);
            Payout.ProcessReturn(first.Id);       // came back
            Sale("U-3X", used: true, invoice: "INV-B"); // legitimately resold

            var warning = SkuCheckService.BuildWarning("U-3X", _skuCheck.FindPriorSales("U-3X"));

            Assert.False(warning!.IsResaleOfReturnedItem); // the resale is still out there
            Assert.Contains("INV-B", warning.Message);
            Assert.Contains("were returned", warning.Message); // and the returned one is listed separately
        }

        [Fact]
        public void UnsavedDuplicatesOnTheSameReceipt_AreAlwaysDuplicates()
        {
            var warning = SkuCheckService.BuildWarning("U-NEW", [], ["Already on this receipt (not yet saved) · Body"]);

            Assert.NotNull(warning);
            Assert.False(warning.IsResaleOfReturnedItem);
        }

        [Fact]
        public void NoMatches_NoWarning_AndBlankSkusNeverMatch()
        {
            Sale("", used: true);

            Assert.Empty(_skuCheck.FindPriorSales("   "));
            Assert.Null(SkuCheckService.BuildWarning("U-NONE", _skuCheck.FindPriorSales("U-NONE")));
        }

        [Fact]
        public void AnItemNeverMatchesItself_AndReturnLinesAreNotMatches()
        {
            var sale = Sale("U-SELF", used: true, PayoutStatus.Paid);
            Payout.ProcessReturn(sale, 100m);

            Assert.Empty(_skuCheck.FindPriorSales("U-SELF", [sale.Id]));
            Assert.Single(_skuCheck.FindPriorSales("U-SELF"));
        }
    }
}

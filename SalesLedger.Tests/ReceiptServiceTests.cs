using SalesLedger.Core.Models;
using SalesLedger.Core.Services;

namespace SalesLedger.Tests
{
    /// <summary>Editing multi-item receipts must never change paid records.</summary>
    public class ReceiptServiceTests : LedgerTestBase
    {
        [Fact]
        public void LoadReceipt_SplitsPendingItemsFromLockedOnes()
        {
            var pending = AddSale(NewLens(500m, "INV-R"));
            var paid = AddSale(NewLens(700m, "INV-R"), PayoutStatus.Paid);
            Payout.ProcessReturn(paid, 100m); // pending return line on the same invoice

            var receipt = Receipts.LoadReceipt("INV-R");

            Assert.Equal([pending.Id], receipt.EditableItems.Select(s => s.Id));
            Assert.Equal(2, receipt.ReadOnlyItems.Count);
            Assert.Contains(receipt.ReadOnlyItems, s => s.Id == paid.Id);
            Assert.Contains(receipt.ReadOnlyItems, s => s.IsReturn);
        }

        [Fact]
        public void SavingAReceipt_LeavesPaidItemsAndReturnLinesUntouched()
        {
            // The scenario from the bug: a paid sale and its pending return line share an invoice
            var paidDate = DateTime.Now.AddMonths(-1);
            var paid = AddSale(NewLens(1000m, "INV-B", paidDate), PayoutStatus.Paid);
            Payout.ProcessReturn(paid, 1000m);
            var returnLine = Assert.Single(ReturnLinesFor(paid));

            // Rules change after the sale was paid
            SetRule("Lens Standard Rule", r => r.RuleValue = 0.06m);

            // Save the receipt with a new item and today's date
            var newItem = NewLens(200m, "INV-B");
            Receipts.SaveReceipt("INV-B", [newItem], [], []);

            var paidAfter = Reload(paid);
            Assert.Equal(paidDate, paidAfter.TransactionDate, TimeSpan.FromSeconds(1));
            Assert.Equal(80.00m, paidAfter.CalculatedCommission);
            Assert.Equal(PayoutStatus.Paid, paidAfter.Status);

            var lineAfter = Reload(returnLine);
            Assert.Equal(-1000m, lineAfter.SalePrice);
            Assert.Equal(-80.00m, lineAfter.CalculatedCommission);

            Assert.Equal(12.00m, Reload(newItem).CalculatedCommission); // 6%
        }

        [Fact]
        public void SaveReceipt_IgnoresLockedItemsEvenIfPassedAsEditable()
        {
            var paid = AddSale(NewLens(1000m, "INV-G"), PayoutStatus.Paid);
            var stale = Reload(paid);
            stale.SalePrice = 1m;

            Receipts.SaveReceipt("INV-G", [stale], [], []);

            Assert.Equal(1000m, Reload(paid).SalePrice);
        }

        [Fact]
        public void StagedReturnOnPaidItem_AddsAReturnLine_InsteadOfChangingTheSale()
        {
            var paid = AddSale(NewLens(1000m, "INV-S"), PayoutStatus.Paid);

            Receipts.SaveReceipt("INV-S", [], [], [new StagedReturn(paid.Id, 1000m, DateTime.Now)]);

            var after = Reload(paid);
            Assert.Equal(PayoutStatus.Paid, after.Status);       // not flipped to ReturnedBeforePayout
            Assert.Equal(80.00m, after.CalculatedCommission);    // not zeroed
            var line = Assert.Single(ReturnLinesFor(paid));
            Assert.Equal(-80.00m, line.CalculatedCommission);    // clawed back in the current period
        }

        [Fact]
        public void StagedPartialReturnOnPendingItem_IsAppliedAfterTheItemIsSaved()
        {
            var item = NewLens(1000m, "INV-N"); // new, not yet in the database

            Receipts.SaveReceipt("INV-N", [item], [], [new StagedReturn(item.Id, 250m, DateTime.Now)]);

            Assert.Equal(PayoutStatus.Pending, Reload(item).Status);
            Assert.Equal(-20.00m, Assert.Single(ReturnLinesFor(item)).CalculatedCommission);
        }

        [Fact]
        public void InvalidStagedReturn_RejectsTheWholeSave_BeforeWritingAnything()
        {
            var item = NewLens(1000m, "INV-V");

            var ex = Assert.Throws<InvalidOperationException>(() =>
                Receipts.SaveReceipt("INV-V", [item], [], [new StagedReturn(item.Id, 5000m, DateTime.Now)]));

            Assert.Contains("Test Lens", ex.Message);
            Assert.Null(Reload(item));
        }

        [Fact]
        public void RemovedItems_AreDeletedWithTheirReturnLines_ButPaidItemsCannotBeRemoved()
        {
            var pending = AddSale(NewLens(500m, "INV-D"));
            Payout.ProcessReturn(pending, 100m);
            var paid = AddSale(NewLens(700m, "INV-D"), PayoutStatus.Paid);

            Receipts.SaveReceipt("INV-D", [], [pending.Id, paid.Id], []);

            Assert.Null(Reload(pending));
            Assert.Empty(ReturnLinesFor(pending));
            Assert.NotNull(Reload(paid));
        }

        [Fact]
        public void SaveReceipt_StampsInvoiceNumber_ButKeepsEachItemsOwnDate()
        {
            // An item added to an old receipt can carry today's date while the original keeps the receipt date
            var receiptDate = new DateTime(2026, 10, 15, 14, 0, 0);
            var original = NewLens(100m, "old", receiptDate);
            var addedToday = new StandardSale { Category = "Mirrorless", ProductName = "Body", SalePrice = 2000m, TransactionDate = DateTime.Now };

            Receipts.SaveReceipt("INV-NEW", [original, addedToday], [], []);

            Assert.All(new[] { Reload(original), Reload(addedToday) }, s => Assert.Equal("INV-NEW", s.InvoiceNumber));
            Assert.Equal(receiptDate, Reload(original).TransactionDate);
            Assert.Equal(DateTime.Today, Reload(addedToday).TransactionDate.Date);
            Assert.Equal(100.00m, Reload(addedToday).CalculatedCommission); // 5%
        }

        [Fact]
        public void LoadReceipt_IgnoresCase_AndReportsTheStoredInvoiceAndReceiptDate()
        {
            var first = AddSale(NewLens(500m, "INV-Case", new DateTime(2026, 9, 2, 10, 0, 0)), PayoutStatus.Paid);
            AddSale(NewLens(300m, "INV-Case", new DateTime(2026, 9, 2, 11, 0, 0)));
            Payout.ProcessReturn(first, 100m, DateTime.Now); // later return line must not move the receipt date

            var receipt = Receipts.LoadReceipt("  inv-case ");

            Assert.True(receipt.Exists);
            Assert.Equal("INV-Case", receipt.InvoiceNumber);
            Assert.Equal(new DateTime(2026, 9, 2, 10, 0, 0), receipt.ReceiptDate);
            Assert.Single(receipt.EditableItems);
            Assert.Equal(2, receipt.ReadOnlyItems.Count);
        }

        [Fact]
        public void LoadReceipt_ForUnknownInvoice_IsEmpty()
        {
            AddSale(NewLens(500m, "INV-1"));

            var receipt = Receipts.LoadReceipt("INV-12");

            Assert.False(receipt.Exists);
            Assert.Null(receipt.ReceiptDate);
        }

        [Fact]
        public void ProductSuggestions_ComeFromTheLedger_MostUsedFirst_WithTheirLastCategory()
        {
            AddSale(new StandardSale { ProductName = "Sony FE 85mm", Category = "Lens", SalePrice = 500m, TransactionDate = DateTime.Now.AddDays(-9) });
            AddSale(new StandardSale { ProductName = "sony fe 85mm ", Category = "Mirrorless", SalePrice = 500m, TransactionDate = DateTime.Now.AddDays(-1) });
            AddSale(new StandardSale { ProductName = "Fuji X100VI", Category = "Point and Shoot", SalePrice = 1600m });
            AddSale(new WarrantySale { ProductName = "3yr Plan", Category = "Warranty", WarrantyTypeName = "Sony", SalePrice = 200m });
            var paid = AddSale(NewLens(900m), PayoutStatus.Paid);
            Payout.ProcessReturn(paid, 100m); // "[RETURN] - ..." must not be suggested

            var suggestions = Receipts.GetProductSuggestions();

            Assert.Equal("sony fe 85mm", suggestions[0].Name);          // used twice, latest spelling
            Assert.Equal("Mirrorless", suggestions[0].Category);        // category from the latest sale
            Assert.DoesNotContain(suggestions, s => s.Name.StartsWith("[RETURN]"));
            var plan = Assert.Single(suggestions, s => s.IsWarranty);
            Assert.Equal("Sony", plan.WarrantyTypeName);
            Assert.Equal(4, suggestions.Count); // 85mm, X100VI, Test Lens, 3yr Plan
        }
    }
}

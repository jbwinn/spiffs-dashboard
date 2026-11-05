using SalesLedger.Core.Models;

namespace SalesLedger.Tests
{
    /// <summary>Full, partial and multiple returns; editing and deleting return lines.</summary>
    public class ReturnsTests : LedgerTestBase
    {
        [Fact]
        public void PartialReturn_OnPaidSale_ClawsBackProportionalCommission_AndLeavesOriginalUntouched()
        {
            var sale = AddSale(NewLens(1000m), PayoutStatus.Paid); // 8% = 80.00

            Payout.ProcessReturn(sale, 250m);

            var original = Reload(sale);
            Assert.Equal(PayoutStatus.Paid, original.Status);
            Assert.Equal(80.00m, original.CalculatedCommission);

            var line = Assert.Single(ReturnLinesFor(sale));
            Assert.Equal(-250m, line.SalePrice);
            Assert.Equal(-20.00m, line.CalculatedCommission);
            Assert.Equal(PayoutStatus.Pending, line.Status);
            Assert.IsType<StandardSale>(line);
        }

        [Fact]
        public void PartialReturn_OnPendingSale_AddsReturnLine_AndKeepsSalePending()
        {
            var sale = AddSale(NewLens(1000m));

            Payout.ProcessReturn(sale, 400m);

            Assert.Equal(PayoutStatus.Pending, Reload(sale).Status);
            var line = Assert.Single(ReturnLinesFor(sale));
            Assert.Equal(-400m, line.SalePrice);
            Assert.Equal(-32.00m, line.CalculatedCommission);
        }

        [Fact]
        public void FullReturn_OnPendingSale_MarksReturnedBeforePayout_WithoutReturnLine()
        {
            var sale = AddSale(NewLens(1000m));

            Payout.ProcessReturn(sale, 1000m);

            var original = Reload(sale);
            Assert.Equal(PayoutStatus.ReturnedBeforePayout, original.Status);
            Assert.Equal(0m, original.CalculatedCommission);
            Assert.Empty(ReturnLinesFor(sale));
        }

        [Fact]
        public void MultiplePartialReturns_FinalReturnTakesExactlyWhatIsLeft()
        {
            var sale = AddSale(NewLens(999.99m), PayoutStatus.Paid); // 8% = 80.00 (79.9992 rounded)
            Assert.Equal(80.00m, sale.CalculatedCommission);

            Payout.ProcessReturn(sale, 333.33m, DateTime.Now.AddMinutes(-2));
            Payout.ProcessReturn(sale, 333.33m, DateTime.Now.AddMinutes(-1));
            Payout.ProcessReturn(sale, 333.33m, DateTime.Now);

            var lines = ReturnLinesFor(sale);
            Assert.Equal(3, lines.Count);
            Assert.Equal(-26.67m, lines[0].CalculatedCommission);
            Assert.Equal(-26.67m, lines[1].CalculatedCommission);
            Assert.Equal(-26.66m, lines[2].CalculatedCommission); // true-up: no stranded pennies
            Assert.Equal(-80.00m, lines.Sum(l => l.CalculatedCommission));
            Assert.Equal(-999.99m, lines.Sum(l => l.SalePrice));
        }

        [Fact]
        public void Return_ExceedingRemainingAmount_IsRejected_AndNothingIsSaved()
        {
            var sale = AddSale(NewLens(1000m), PayoutStatus.Paid);
            Payout.ProcessReturn(sale, 700m);

            var ex = Assert.Throws<InvalidOperationException>(() => Payout.ProcessReturn(sale, 400m));
            Assert.Contains("remaining", ex.Message);
            Assert.Single(ReturnLinesFor(sale));
        }

        [Fact]
        public void Return_OnFullyRefundedSale_IsRejected()
        {
            var sale = AddSale(NewLens(1000m), PayoutStatus.Paid);
            Payout.ProcessReturn(sale.Id); // full

            Assert.Throws<InvalidOperationException>(() => Payout.ProcessReturn(sale.Id));
            Assert.Single(ReturnLinesFor(sale));
        }

        [Fact]
        public void FlatRate_FirstReturnTakesFullCommission_LaterReturnsTakeNothing()
        {
            var sale = AddSale(new StandardSale
            {
                InvoiceNumber = "INV-F", Category = "Tripod", ProductName = "Tripod", SalePrice = 300m,
            }, PayoutStatus.Paid);
            sale.CalculatedCommission = 25m;
            sale.AppliedCalculationType = PayoutType.FlatRate;
            Lite.Sales.Update(sale);

            Payout.ProcessReturn(sale, 100m, DateTime.Now.AddMinutes(-1));
            Payout.ProcessReturn(sale, 50m, DateTime.Now);

            var lines = ReturnLinesFor(sale);
            Assert.Equal(-25m, lines[0].CalculatedCommission);
            Assert.Equal(0m, lines[1].CalculatedCommission);
        }

        [Fact]
        public void FlatRate_IsInferredFromRules_ForRecordsWithoutAStoredPayoutType()
        {
            var settings = Lite.GetUserSettings();
            settings.ActiveRules!.Insert(0, new CommissionRule
            {
                RuleName = "Tripod Flat", Scope = RuleScope.CategorySpecific, TargetCategory = "Tripod",
                CalculationType = PayoutType.FlatRate, RuleValue = 25m, PriorityOrder = -1
            });
            Lite.SaveUserSettings(settings);

            // A record saved before AppliedCalculationType existed
            var sale = new StandardSale { InvoiceNumber = "INV-L", Category = "Tripod", ProductName = "Tripod", SalePrice = 300m, CalculatedCommission = 25m, Status = PayoutStatus.Paid };
            Lite.Sales.Insert(sale);

            Payout.ProcessReturn(sale, 30m);

            Assert.Equal(-25m, Assert.Single(ReturnLinesFor(sale)).CalculatedCommission);
        }

        [Fact]
        public void WarrantyPartialReturn_ScalesWholesaleAndNetMarginCommission()
        {
            var sale = AddSale(new WarrantySale
            {
                InvoiceNumber = "INV-W", ProductName = "3yr Plan", Category = "Warranty", WarrantyTypeName = "Sony",
                SalePrice = 300m, ManufacturerPrice = 120m
            }, PayoutStatus.Paid); // 10% of (300 - 120) = 18.00
            Assert.Equal(18.00m, sale.CalculatedCommission);

            Payout.ProcessReturn(sale, 150m);

            var line = Assert.IsType<WarrantySale>(Assert.Single(ReturnLinesFor(sale)));
            Assert.Equal(-9.00m, line.CalculatedCommission);
            Assert.Equal(-60.00m, line.ManufacturerPrice);
        }

        [Fact]
        public void UpdateReturnLine_ChangesRefund_AndRecalculatesClawback()
        {
            var sale = AddSale(NewLens(1000m), PayoutStatus.Paid);
            Payout.ProcessReturn(sale, 250m);
            var line = Assert.Single(ReturnLinesFor(sale));

            Payout.UpdateReturnLine(line.Id, 500m);

            var updated = Reload(line);
            Assert.Equal(-500m, updated.SalePrice);
            Assert.Equal(-40.00m, updated.CalculatedCommission);
        }

        [Fact]
        public void UpdateReturnLine_CannotExceedWhatOtherReturnsLeave()
        {
            var sale = AddSale(NewLens(1000m), PayoutStatus.Paid);
            Payout.ProcessReturn(sale, 600m, DateTime.Now.AddMinutes(-1));
            Payout.ProcessReturn(sale, 100m, DateTime.Now);
            var second = ReturnLinesFor(sale)[1];

            Assert.Throws<InvalidOperationException>(() => Payout.UpdateReturnLine(second.Id, 500m));
            Payout.UpdateReturnLine(second.Id, 400m); // exactly the remainder is fine

            Assert.Equal(-1000m, ReturnLinesFor(sale).Sum(l => l.SalePrice));
            Assert.Equal(-80.00m, ReturnLinesFor(sale).Sum(l => l.CalculatedCommission));
        }

        [Fact]
        public void UpdateReturnLine_OnPaidReturnLine_IsRejected()
        {
            var sale = AddSale(NewLens(1000m), PayoutStatus.Paid);
            Payout.ProcessReturn(sale, 250m);
            Payout.CloseCurrentPayPeriod("Period with return");
            var line = Assert.Single(ReturnLinesFor(sale));
            Assert.Equal(PayoutStatus.Paid, line.Status);

            Assert.Throws<InvalidOperationException>(() => Payout.UpdateReturnLine(line.Id, 100m));
            Assert.Equal(-250m, Reload(line).SalePrice);
        }

        [Fact]
        public void PaidReturnLines_KeepTheirAmounts_AndCountTowardsLaterReturns()
        {
            var sale = AddSale(NewLens(1000m), PayoutStatus.Paid);
            Payout.ProcessReturn(sale, 500m, DateTime.Now.AddDays(-1));
            Payout.CloseCurrentPayPeriod("First period"); // locks the first return line

            Payout.ProcessReturn(sale, 500m);

            var lines = ReturnLinesFor(sale);
            Assert.Equal(-40.00m, lines[0].CalculatedCommission);
            Assert.Equal(-40.00m, lines[1].CalculatedCommission);
        }

        [Fact]
        public void DeletingAReturnLine_RebalancesTheRemainingReturns()
        {
            var sale = AddSale(new StandardSale { InvoiceNumber = "INV-F", Category = "Tripod", ProductName = "Tripod", SalePrice = 300m }, PayoutStatus.Paid);
            sale.CalculatedCommission = 25m;
            sale.AppliedCalculationType = PayoutType.FlatRate;
            Lite.Sales.Update(sale);

            Payout.ProcessReturn(sale, 100m, DateTime.Now.AddMinutes(-1));
            Payout.ProcessReturn(sale, 50m, DateTime.Now);
            var first = ReturnLinesFor(sale)[0];

            Payout.DeleteSale(first.Id);

            // The surviving return now carries the flat-rate clawback
            Assert.Equal(-25m, Assert.Single(ReturnLinesFor(sale)).CalculatedCommission);
        }

        [Fact]
        public void DeletingAPendingSale_AlsoDeletesItsReturnLines()
        {
            var sale = AddSale(NewLens(1000m));
            Payout.ProcessReturn(sale, 200m);

            Payout.DeleteSale(sale.Id);

            Assert.Null(Reload(sale));
            Assert.Empty(Lite.Sales.Find(x => x.IsReturn).ToList());
        }

        [Fact]
        public void DeletingAPaidSale_IsRejected()
        {
            var sale = AddSale(NewLens(1000m), PayoutStatus.Paid);

            Assert.Throws<InvalidOperationException>(() => Payout.DeleteSale(sale.Id));
            Assert.NotNull(Reload(sale));
        }

        [Fact]
        public void PreviewReturn_DoesNotSaveAnything()
        {
            var sale = AddSale(NewLens(1000m), PayoutStatus.Paid);

            var preview = Payout.PreviewReturn(sale, 250m);

            Assert.True(preview.IsValid);
            Assert.Equal(20.00m, preview.CommissionClawback);
            Assert.Equal(1000m, preview.Remaining);
            Assert.Empty(ReturnLinesFor(sale));
        }

        [Fact]
        public void ReturnLines_CannotThemselvesBeReturned()
        {
            var sale = AddSale(NewLens(1000m), PayoutStatus.Paid);
            Payout.ProcessReturn(sale, 250m);
            var line = Assert.Single(ReturnLinesFor(sale));

            Assert.False(line.CanProcessReturn);
            Assert.False(Payout.PreviewReturn(line, 10m).IsValid);
        }
    }
}

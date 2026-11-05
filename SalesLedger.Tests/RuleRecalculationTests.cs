using SalesLedger.Core.Models;

namespace SalesLedger.Tests
{
    /// <summary>A rule change re-prices everything still pending in the current pay period, and nothing that is paid.</summary>
    public class RuleRecalculationTests : LedgerTestBase
    {
        [Fact]
        public void RuleChange_RecalculatesPendingSales_ButNotPaidSales()
        {
            var pending = AddSale(NewLens(1000m, "INV-P"));
            var paid = AddSale(NewLens(1000m, "INV-X"), PayoutStatus.Paid);

            SetRule("Lens Standard Rule", r => r.RuleValue = 0.06m);
            var changed = Payout.RecalculatePendingCommissions();

            Assert.Equal(1, changed);
            Assert.Equal(60.00m, Reload(pending).CalculatedCommission);
            Assert.Equal(80.00m, Reload(paid).CalculatedCommission);
        }

        [Fact]
        public void RuleChange_IncludesPendingSalesFromEarlierMonths()
        {
            // "Current pay period" = everything not yet finalized, regardless of date
            var oldPending = AddSale(NewLens(1000m, "INV-OLD", DateTime.Now.AddMonths(-2)));

            SetRule("Lens Standard Rule", r => r.RuleValue = 0.06m);
            Payout.RecalculatePendingCommissions();

            Assert.Equal(60.00m, Reload(oldPending).CalculatedCommission);
        }

        [Fact]
        public void RuleChange_LeavesReturnsAgainstPaidSalesAlone()
        {
            var paid = AddSale(NewLens(1000m), PayoutStatus.Paid);
            Payout.ProcessReturn(paid, 250m); // pending return line, -20.00 (reverses what was actually paid)

            SetRule("Lens Standard Rule", r => r.RuleValue = 0.06m);
            Payout.RecalculatePendingCommissions();

            Assert.Equal(-20.00m, Assert.Single(ReturnLinesFor(paid)).CalculatedCommission);
        }

        [Fact]
        public void RuleChange_RebalancesPartialReturnsOnPendingSales()
        {
            var pending = AddSale(NewLens(1000m));
            Payout.ProcessReturn(pending, 250m); // -20.00 at 8%

            SetRule("Lens Standard Rule", r => r.RuleValue = 0.06m);
            Payout.RecalculatePendingCommissions();

            Assert.Equal(60.00m, Reload(pending).CalculatedCommission);
            Assert.Equal(-15.00m, Assert.Single(ReturnLinesFor(pending)).CalculatedCommission);
        }

        [Fact]
        public void RuleChange_DoesNotTouchSalesReturnedBeforePayout()
        {
            var returned = AddSale(NewLens(1000m));
            Payout.ProcessReturn(returned.Id); // full return → ReturnedBeforePayout, $0

            SetRule("Lens Standard Rule", r => r.RuleValue = 0.06m);
            Payout.RecalculatePendingCommissions();

            Assert.Equal(0m, Reload(returned).CalculatedCommission);
            Assert.Equal(PayoutStatus.ReturnedBeforePayout, Reload(returned).Status);
        }

        [Fact]
        public void ApplyCommission_RecordsThePayoutTypeOfTheMatchingRule()
        {
            var lens = AddSale(NewLens(1000m));
            var unmatched = AddSale(new StandardSale { Category = "Tripod", SalePrice = 100m });

            Assert.Equal(PayoutType.PercentageOfPrice, Reload(lens).AppliedCalculationType);
            Assert.Null(Reload(unmatched).AppliedCalculationType);
            Assert.Equal(0m, Reload(unmatched).CalculatedCommission);
        }

        [Fact]
        public void NewSales_DefaultToLocalTime()
        {
            // Models used to default to UTC, which DuckDB would store hours off from LiteDB's local time
            Assert.Equal(DateTimeKind.Local, new StandardSale().TransactionDate.Kind);
        }
    }
}

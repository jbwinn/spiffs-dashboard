using System;
using System.Collections.Generic;
using Xunit;
using SalesLedger.Core.Models;
using SalesLedger.Core.Services;

namespace SalesLedger.Tests
{
    public class CommissionProcessorTests
    {
        private readonly CommissionProcessor _processor = new();
        private readonly List<CommissionRule> _defaultRules = new()
        {
            new CommissionRule
            {
                RuleId = "rule-used",
                RuleName = "Default Used Gear Rule",
                PriorityOrder = 0,
                Scope = RuleScope.AllUsed,
                CalculationType = PayoutType.PercentageOfPrice,
                RuleValue = 0.03m // 3%
            },
            new CommissionRule
            {
                RuleId = "rule-warranty",
                RuleName = "Default Warranty Rule",
                PriorityOrder = 1,
                Scope = RuleScope.AllWarranty,
                CalculationType = PayoutType.PercentageOfPrice,
                RuleValue = 0.10m // 10%
            },
            new CommissionRule
            {
                RuleId = "rule-cameras",
                RuleName = "Cameras Standard Rule",
                PriorityOrder = 2,
                Scope = RuleScope.CategorySpecific,
                TargetCategory = "Cameras",
                CalculationType = PayoutType.PercentageOfPrice,
                RuleValue = 0.05m // 5%
            },
            new CommissionRule
            {
                RuleId = "rule-lenses",
                RuleName = "Lenses Standard Rule",
                PriorityOrder = 3,
                Scope = RuleScope.CategorySpecific,
                TargetCategory = "Lenses",
                CalculationType = PayoutType.PercentageOfPrice,
                RuleValue = 0.08m // 8%
            }
        };

        [Fact]
        public void StandardSale_UsedGear_TriggersUsedGearRule_First()
        {
            // Standard sale, Category Cameras, but IS used gear.
            // Should match Used Gear (3%) instead of Cameras (5%) because of PriorityOrder 0 vs 2.
            var sale = new StandardSale
            {
                Id = Guid.NewGuid(),
                Category = "Cameras",
                SalePrice = 1000m,
                IsUsedGear = true
            };

            var commission = _processor.CalculateLineItem(sale, _defaultRules);

            // 3% of $1000 = $30
            Assert.Equal(30.00m, commission);
        }

        [Fact]
        public void StandardSale_NewGear_CamerasCategory_TriggersCamerasRule()
        {
            // Standard sale, Category Cameras, NOT used gear.
            // Should match Cameras rule (5%).
            var sale = new StandardSale
            {
                Id = Guid.NewGuid(),
                Category = "Cameras",
                SalePrice = 1000m,
                IsUsedGear = false
            };

            var commission = _processor.CalculateLineItem(sale, _defaultRules);

            // 5% of $1000 = $50
            Assert.Equal(50.00m, commission);
        }

        [Fact]
        public void StandardSale_NewGear_LensesCategory_TriggersLensesRule()
        {
            // Standard sale, Category Lenses, NOT used.
            // Should match Lenses rule (8%).
            var sale = new StandardSale
            {
                Id = Guid.NewGuid(),
                Category = "Lenses",
                SalePrice = 500m,
                IsUsedGear = false
            };

            var commission = _processor.CalculateLineItem(sale, _defaultRules);

            // 8% of $500 = $40
            Assert.Equal(40.00m, commission);
        }

        [Fact]
        public void WarrantySale_TriggersWarrantyPercentageRule()
        {
            // Warranty sale. Should match Warranty percentage rule (10%).
            var sale = new WarrantySale
            {
                Id = Guid.NewGuid(),
                Category = "Accessories",
                SalePrice = 150m,
                WarrantyTypeName = "3-Year Extension",
                ManufacturerPrice = 50m
            };

            var commission = _processor.CalculateLineItem(sale, _defaultRules);

            // 10% of 150m = 15m
            Assert.Equal(15.00m, commission);
        }

        [Fact]
        public void StandardSale_NoRulesMatch_ReturnsZero()
        {
            // Category "Accessories" with no used flag set.
            // No rule matches "Accessories" in default list.
            var sale = new StandardSale
            {
                Id = Guid.NewGuid(),
                Category = "Accessories",
                SalePrice = 100m,
                IsUsedGear = false
            };

            var commission = _processor.CalculateLineItem(sale, _defaultRules);

            Assert.Equal(0.00m, commission);
        }

        [Fact]
        public void ReturnOffsetSale_KeepsPreCalculatedCommission()
        {
            // Return offsets should retain whatever negative commission was assigned to them
            var sale = new ReturnOffsetSale
            {
                Id = Guid.NewGuid(),
                Category = "Cameras",
                SalePrice = -1000m,
                CalculatedCommission = -50.00m
            };

            var commission = _processor.CalculateLineItem(sale, _defaultRules);

            Assert.Equal(-50.00m, commission);
        }

        [Fact]
        public void WarrantySale_PercentageOfNetProfit_CalculatesCorrectly()
        {
            var netProfitRules = new List<CommissionRule>
            {
                new CommissionRule
                {
                    RuleId = "rule-warranty-netprofit",
                    RuleName = "Warranty Net Profit Rule",
                    PriorityOrder = 0,
                    Scope = RuleScope.AllWarranty,
                    CalculationType = PayoutType.PercentageOfNetProfit,
                    RuleValue = 0.10m // 10% of profit
                }
            };

            var sale = new WarrantySale
            {
                Id = Guid.NewGuid(),
                Category = "Warranty",
                SalePrice = 150m,
                WarrantyTypeName = "Nikon",
                ManufacturerPrice = 50m // profit = 150 - 50 = 100
            };

            var commission = _processor.CalculateLineItem(sale, netProfitRules);

            // 10% of $100 = $10
            Assert.Equal(10.00m, commission);
        }

        [Fact]
        public void EbaySale_AlwaysCalculatesTenPercentCommission()
        {
            var sale = new EbaySale
            {
                Id = Guid.NewGuid(),
                Category = "Cameras",
                SalePrice = 1200m,
                IsUsedGear = true
            };

            // Calculate even with rule list (Ebay sales should bypass rules waterfall)
            var commission = _processor.CalculateLineItem(sale, _defaultRules);

            // 10% of $1200 = $120
            Assert.Equal(120.00m, commission);
        }
    }
}

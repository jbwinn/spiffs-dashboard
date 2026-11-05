using System;
using System.Collections.Generic;
using System.Linq;
using SalesLedger.Core.Models;

namespace SalesLedger.Core.Services
{
    public class CommissionProcessor
    {
        /// <summary>
        /// Returns the first rule in the priority waterfall that matches the sale, or null if none match.
        /// </summary>
        public CommissionRule? FindMatchingRule(SaleRecord sale, IEnumerable<CommissionRule> activeRules)
        {
            foreach (var rule in activeRules.OrderBy(r => r.PriorityOrder))
            {
                bool isMatch = rule.Scope switch
                {
                    RuleScope.AllWarranty => sale is WarrantySale,
                    RuleScope.AllUsed => sale is StandardSale { IsUsedGear: true },
                    RuleScope.AllEbay => sale is EbaySale,
                    RuleScope.CategorySpecific => string.Equals(sale.Category, rule.TargetCategory, StringComparison.OrdinalIgnoreCase),
                    _ => false
                };

                if (isMatch)
                {
                    return rule;
                }
            }

            return null;
        }

        public decimal CalculateLineItem(SaleRecord sale, List<CommissionRule> activeRules)
        {
            // Return lines carry their own clawback, computed by PayoutLedgerService
            if (IsReturnRecord(sale))
            {
                return sale.CalculatedCommission;
            }

            var rule = FindMatchingRule(sale, activeRules);
            return rule == null ? 0.00m : Calculate(sale, rule);
        }

        /// <summary>
        /// Calculates the commission and records which payout method produced it.
        /// Return lines are left untouched.
        /// </summary>
        public void ApplyCommission(SaleRecord sale, List<CommissionRule> activeRules)
        {
            if (IsReturnRecord(sale))
            {
                return;
            }

            var rule = FindMatchingRule(sale, activeRules);
            sale.CalculatedCommission = rule == null ? 0.00m : Calculate(sale, rule);
            sale.AppliedCalculationType = rule?.CalculationType;
        }

        private static bool IsReturnRecord(SaleRecord sale) => sale.IsReturn || sale is ReturnOffsetSale;

        private static decimal Calculate(SaleRecord sale, CommissionRule rule)
        {
            return rule.CalculationType switch
            {
                PayoutType.PercentageOfPrice => Math.Round(sale.SalePrice * rule.RuleValue, 2),
                PayoutType.PercentageOfNetProfit => sale is WarrantySale warrantySale
                    ? Math.Round(warrantySale.NetMargin * rule.RuleValue, 2)
                    : Math.Round(sale.SalePrice * rule.RuleValue, 2),
                _ => rule.RuleValue
            };
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using SalesLedger.Core.Models;

namespace SalesLedger.Core.Services
{
    public class CommissionProcessor
    {
        public decimal CalculateLineItem(SaleRecord sale, List<CommissionRule> activeRules)
        {
            // Return offsets carry their own fixed pre-calculated negative commission
            if (sale.IsReturn || sale is ReturnOffsetSale)
            {
                return sale.CalculatedCommission;
            }

            // Enforce sorting sequence matching the defined waterfall ranking hierarchy
            var waterfall = activeRules.OrderBy(r => r.PriorityOrder).ToList();

            foreach (var rule in waterfall)
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
                    if (rule.CalculationType == PayoutType.PercentageOfPrice)
                    {
                        return Math.Round(sale.SalePrice * rule.RuleValue, 2);
                    }
                    else if (rule.CalculationType == PayoutType.PercentageOfNetProfit)
                    {
                        if (sale is WarrantySale warrantySale)
                        {
                            return Math.Round(warrantySale.NetMargin * rule.RuleValue, 2);
                        }
                        else
                        {
                            return Math.Round(sale.SalePrice * rule.RuleValue, 2);
                        }
                    }
                    else
                    {
                        return rule.RuleValue;
                    }
                }
            }

            return 0.00m; // Fallback value if zero conditional rules evaluate to true
        }
    }
}

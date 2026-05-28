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
            if (sale is ReturnOffsetSale)
            {
                return sale.CalculatedCommission;
            }

            // Enforce sorting sequence matching the defined waterfall ranking hierarchy
            var waterfall = activeRules.OrderBy(r => r.PriorityOrder).ToList();

            foreach (var rule in waterfall)
            {
                bool isMatch = false;

                switch (rule.Scope)
                {
                    case RuleScope.AllWarranty:
                        isMatch = (sale is WarrantySale);
                        break;
                    case RuleScope.AllUsed:
                        isMatch = (sale is StandardSale standardItem && standardItem.IsUsedGear);
                        break;
                    case RuleScope.AllEbay:
                        isMatch = (sale is EbaySale);
                        break;
                    case RuleScope.CategorySpecific:
                        isMatch = string.Equals(sale.Category, rule.TargetCategory, StringComparison.OrdinalIgnoreCase);
                        break;
                }

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

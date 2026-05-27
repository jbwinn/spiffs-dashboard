using System;
using System.Collections.Generic;

namespace SalesLedger.Core.Models
{
    public enum SaleType { Standard, Warranty, ReturnOffset }
    public enum PayoutStatus { Pending, Paid, ReturnedBeforePayout }
    public enum PayoutType { PercentageOfPrice, FlatRate, PercentageOfNetProfit }
    public enum RuleScope { AllUsed, AllWarranty, CategorySpecific }

    public abstract class SaleRecord 
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public SaleType RecordType { get; set; }
        public PayoutStatus Status { get; set; } = PayoutStatus.Pending;
        public Guid? AssociatedReportId { get; set; } // Identifies the report that locked this record
        public DateTime TransactionDate { get; set; } = DateTime.UtcNow;
        public string InvoiceNumber { get; set; } = string.Empty;
        public string Sku { get; set; } = string.Empty;
        public string ProductName { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty; // Matches AppCategory constraints
        public decimal SalePrice { get; set; }
        public decimal CalculatedCommission { get; set; }
    }

    public class StandardSale : SaleRecord 
    {
        public bool IsUsedGear { get; set; }
        public StandardSale() => RecordType = SaleType.Standard;
    }

    public class WarrantySale : SaleRecord 
    {
        public string WarrantyTypeName { get; set; } = string.Empty; // Matches AppWarrantyType constraints
        public decimal ManufacturerPrice { get; set; } // The wholesale cost to the business
        public decimal NetMargin => SalePrice - ManufacturerPrice;
        public WarrantySale() => RecordType = SaleType.Warranty;
    }

    public class ReturnOffsetSale : SaleRecord
    {
        public Guid OriginalSaleId { get; set; }
        public ReturnOffsetSale() => RecordType = SaleType.ReturnOffset;
    }

    public class PayoutReport
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public DateTime ReportGeneratedTimestamp { get; set; } = DateTime.UtcNow;
        public string ReportName { get; set; } = string.Empty; // e.g., "June 2026 Submission"
        public decimal TotalCommissionCalculated { get; set; }
        public List<Guid> LockedSaleIds { get; set; } = new();
    }

    public class UserSettings 
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string UserDisplayName { get; set; } = string.Empty;
        public List<AppCategory> ProductCategories { get; set; } = new();
        public List<AppWarrantyType> WarrantyTypes { get; set; } = new();
        public List<CommissionRule> ActiveRules { get; set; } = new();
    }

    public class AppCategory
    {
        public string Name { get; set; } = string.Empty;
        public bool IsSystemPreset { get; set; } // If true, UI blocks modification/removal
        public bool IsActive { get; set; } = true; // Handles Soft-Delete states
    }

    public class AppWarrantyType
    {
        public string Name { get; set; } = string.Empty;
        public bool IsSystemPreset { get; set; }
        public bool IsActive { get; set; } = true;
    }

    public class CommissionRule
    {
        public string RuleId { get; set; } = Guid.NewGuid().ToString("N");
        public string RuleName { get; set; } = string.Empty;
        public int PriorityOrder { get; set; } // Execution ranking index (0 = Top Precedence)
        public RuleScope Scope { get; set; }
        public string TargetCategory { get; set; } = string.Empty; // Used only if Scope == CategorySpecific
        public PayoutType CalculationType { get; set; }
        public decimal RuleValue { get; set; } // Stores decimal rates (e.g., 0.12) or flat-rates (e.g., 25.00)
    }
}

using System;
using System.Collections.Generic;

namespace SalesLedger.Core.Models
{
    public enum SaleType { Standard, Warranty, ReturnOffset, Ebay }
    public enum PayoutStatus { Pending, Paid, ReturnedBeforePayout }
    public enum PayoutType { PercentageOfPrice, FlatRate, PercentageOfNetProfit }
    public enum RuleScope { AllUsed, AllWarranty, CategorySpecific, AllEbay }

    public abstract class SaleRecord 
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public SaleType RecordType { get; init; }
        public PayoutStatus Status { get; set; } = PayoutStatus.Pending;
        public Guid? AssociatedReportId { get; set; } // Identifies the report that locked this record
        // Local time everywhere: LiteDB stores UTC and reads back local, while DuckDB stores the
        // wall-clock value it is given. Defaulting to UTC here would put new records hours off in analytics.
        public DateTime TransactionDate { get; set; } = DateTime.Now;

        // Text fields never hold null: LiteDB (by default, and in records saved before that was turned off)
        // reads blank text back as null, which would crash every .Trim()/.Contains() on a blank SKU.
        public string InvoiceNumber { get; set => field = value ?? string.Empty; } = string.Empty;
        public string Sku { get; set => field = value ?? string.Empty; } = string.Empty;
        public string ProductName { get; set => field = value ?? string.Empty; } = string.Empty;
        public string Category { get; set => field = value ?? string.Empty; } = string.Empty; // Matches AppCategory constraints
        public decimal SalePrice { get; set; }
        public decimal CalculatedCommission { get; set; }

        // The payout method of the rule that produced CalculatedCommission (null = no rule matched,
        // or the record predates this field). Used to decide how much commission a return claws back.
        public PayoutType? AppliedCalculationType { get; set; }

        // Return tracking properties. A return line is a sale of the same concrete type with
        // IsReturn = true, a negative SalePrice (the refund) and a negative commission (the clawback).
        // Several partial return lines may point at the same OriginalSaleId.
        public bool IsReturn { get; set; }
        public Guid? OriginalSaleId { get; set; }

        // On a return line: true for the line that brings the item's refunds up to its full price, i.e. the
        // moment the unit actually comes back. Partial refunds (customer keeps the item) leave units sold unchanged.
        public bool CompletesRefund { get; set; }

        // UI state helper properties
        public bool IsPending => Status == PayoutStatus.Pending;
        public bool IsReturnedBeforePayout => Status == PayoutStatus.ReturnedBeforePayout;
        public bool IsPaid => Status == PayoutStatus.Paid;
        public bool CanProcessReturn => !IsReturn && RecordType != SaleType.ReturnOffset && Status != PayoutStatus.ReturnedBeforePayout;
        public bool CanOpenReceipt => !IsReturn && RecordType != SaleType.ReturnOffset; // any status: paid receipts can still gain items
        public bool CanEditReturnLine => IsReturn && Status == PayoutStatus.Pending;
    }

    public class StandardSale : SaleRecord 
    {
        public bool IsUsedGear { get; set; }
        public StandardSale() => RecordType = SaleType.Standard;
    }

    public class WarrantySale : SaleRecord 
    {
        public string WarrantyTypeName { get; set => field = value ?? string.Empty; } = string.Empty; // Matches AppWarrantyType constraints
        public decimal ManufacturerPrice { get; set; } // The wholesale cost to the business
        public decimal NetMargin => SalePrice - ManufacturerPrice;
        public WarrantySale() => RecordType = SaleType.Warranty;
    }

    public class ReturnOffsetSale : SaleRecord
    {
        public ReturnOffsetSale() => RecordType = SaleType.ReturnOffset;
    }

    public class EbaySale : SaleRecord
    {
        public bool IsUsedGear { get; set; }
        public EbaySale() => RecordType = SaleType.Ebay;
    }

    public class PayoutReport
    {
        public Guid Id { get; init; } = Guid.NewGuid();

        /// <summary>When the pay period was finalized (submitted). Pay periods don't follow calendar months, so this
        /// (together with the previous report's) is what defines the period.</summary>
        public DateTime ReportGeneratedTimestamp { get; init; } = DateTime.UtcNow;
        public string ReportName { get; init => field = value ?? string.Empty; } = string.Empty; // e.g., "June 2026 Submission"
        public decimal TotalCommissionCalculated { get; init; }
        public List<Guid> LockedSaleIds { get; init; } = [];

        /// <summary>Where the PDF was last saved (empty for reports finalized before reports were stored).</summary>
        public string PdfPath { get; set => field = value ?? string.Empty; } = string.Empty;
    }

    public class UserSettings
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string UserDisplayName { get; set => field = value ?? string.Empty; } = string.Empty;
        public List<AppCategory>? ProductCategories { get; set; } = [];
        public List<AppWarrantyType>? WarrantyTypes { get; set; } = [];
        public List<CommissionRule>? ActiveRules { get; set; } = [];
        public bool AutoUpdateEnabled { get; set; } = true;

        /// <summary>Folder for payout report PDFs. Empty = the default (Documents\Sales Ledger Reports).</summary>
        public string ReportsFolder { get; set => field = value ?? string.Empty; } = string.Empty;
    }

    public class AppCategory
    {
        public string Name { get; set => field = value ?? string.Empty; } = string.Empty;
        public bool IsSystemPreset { get; set; } // If true, UI blocks modification/removal
        public bool IsActive { get; set; } = true; // Handles Soft-Delete states
    }

    public class AppWarrantyType
    {
        public string Name { get; set => field = value ?? string.Empty; } = string.Empty;
        public bool IsSystemPreset { get; set; }
        public bool IsActive { get; set; } = true;
    }

    public class CommissionRule
    {
        public string RuleId { get; set; } = Guid.NewGuid().ToString("N");
        public string RuleName { get; set => field = value ?? string.Empty; } = string.Empty;
        public int PriorityOrder { get; set; } // Execution ranking index (0 = Top Precedence)
        public RuleScope Scope { get; set; }
        public string TargetCategory { get; set => field = value ?? string.Empty; } = string.Empty; // Used only if Scope == CategorySpecific
        public PayoutType CalculationType { get; set; }
        public decimal RuleValue { get; set; } // Stores decimal rates (e.g., 0.12) or flat-rates (e.g., 25.00)
    }
}

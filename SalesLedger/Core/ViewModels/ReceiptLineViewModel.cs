using CommunityToolkit.Mvvm.ComponentModel;
using SalesLedger.Core.Models;

namespace SalesLedger.Core.ViewModels
{
    /// <summary>
    /// One line in the receipt dialog. Pending items are editable; paid items, sales returned before payout
    /// and return lines are read-only (paid items can still be returned).
    /// </summary>
    public partial class ReceiptLineViewModel : ObservableObject
    {
        public ReceiptLineViewModel(SaleRecord sale, bool isEditable, bool isSaved, bool followsReceiptDate)
        {
            Sale = sale;
            IsEditable = isEditable;
            IsSaved = isSaved;
            FollowsReceiptDate = followsReceiptDate;
        }

        public SaleRecord Sale { get; }
        public bool IsEditable { get; }
        public bool IsReadOnly => !IsEditable;

        /// <summary>True for lines that already exist in the database (loaded with the receipt).</summary>
        public bool IsSaved { get; }

        /// <summary>
        /// True when the line's date tracks the receipt's date field. False for items deliberately given their own
        /// date (e.g. added to an old receipt with today's date), and for existing items on a different day.
        /// </summary>
        public bool FollowsReceiptDate { get; }

        /// <summary>Shown when a line's date differs from the receipt's, e.g. "Dated Nov 3".</summary>
        public string DateNote => FollowsReceiptDate || Sale.IsReturn ? string.Empty : $"Dated {Sale.TransactionDate:MMM d, yyyy}";
        public bool HasDateNote => !string.IsNullOrEmpty(DateNote);

        public string StatusText => Sale.IsReturn
            ? (Sale.Status == PayoutStatus.Paid ? "Return (Paid)" : "Return")
            : Sale.Status switch
            {
                PayoutStatus.Paid => "Paid (locked)",
                PayoutStatus.ReturnedBeforePayout => "Returned",
                _ => IsSaved ? "Pending" : "New"
            };

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasStagedReturn))]
        [NotifyPropertyChangedFor(nameof(CanStageReturn))]
        public partial string StagedReturnText { get; set; } = string.Empty;

        public bool HasStagedReturn => !string.IsNullOrEmpty(StagedReturnText);
        public bool CanStageReturn => !HasStagedReturn && Sale.CanProcessReturn;
    }
}

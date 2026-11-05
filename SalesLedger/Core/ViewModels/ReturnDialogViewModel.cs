using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SalesLedger.Core.Models;
using SalesLedger.Core.Services;

namespace SalesLedger.Core.ViewModels
{
    /// <summary>
    /// The confirmation dialog for returns: a new return (full or partial), a change to an existing
    /// return line, or a return staged inside the receipt dialog. It validates and previews the commission
    /// effect; what happens on confirm is supplied by the caller.
    /// </summary>
    public partial class ReturnDialogViewModel(PayoutLedgerService payout) : ObservableObject
    {
        private readonly PayoutLedgerService _payout = payout ?? throw new ArgumentNullException(nameof(payout));

        private SaleRecord? _original;
        private Guid? _editingLineId;
        private Func<decimal, DateTime, string?>? _onConfirm;

        [ObservableProperty] public partial bool IsVisible { get; set; }
        [ObservableProperty] public partial string Title { get; set; } = "Process Return";
        [ObservableProperty] public partial string ProductText { get; set; } = string.Empty;
        [ObservableProperty] public partial string NoticeText { get; set; } = string.Empty;
        [ObservableProperty] public partial string ConfirmButtonText { get; set; } = "Confirm Return";

        [ObservableProperty] public partial string OriginalPriceText { get; set; } = string.Empty;
        [ObservableProperty] public partial string AlreadyRefundedText { get; set; } = string.Empty;
        [ObservableProperty] public partial string RemainingText { get; set; } = string.Empty;

        [ObservableProperty] public partial decimal RefundAmount { get; set; }
        [ObservableProperty] public partial DateTime? ReturnDate { get; set; } = DateTime.Today;

        [ObservableProperty] public partial string CommissionEffectText { get; set; } = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasError))]
        public partial string ErrorMessage { get; set; } = string.Empty;

        public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

        /// <summary>
        /// Opens the dialog for a new return on <paramref name="original"/>, prefilled with the full remaining amount.
        /// <paramref name="onConfirm"/> applies it and returns an error message, or null on success.
        /// </summary>
        public void OpenForReturn(SaleRecord original, Func<decimal, DateTime, string?> onConfirm, bool isStaged = false)
        {
            _original = original;
            _editingLineId = null;
            _onConfirm = onConfirm;

            Title = "Process Return";
            ConfirmButtonText = isStaged ? "Add Return to Receipt" : "Confirm Return";
            NoticeText = isStaged
                ? "The return will be applied when you save the receipt. Lower the refund amount for a partial return."
                : "Lower the refund amount for a partial return. Commission is adjusted in proportion to the refund (flat-rate commission is taken back in full).";

            var preview = _payout.PreviewReturn(original, original.SalePrice);
            Open(original, preview.Remaining > 0 ? preview.Remaining : 0m, DateTime.Today);
        }

        /// <summary>Opens the dialog to change the refund amount on an existing, unpaid return line.</summary>
        public void OpenForEditLine(SaleRecord returnLine, SaleRecord original, Func<decimal, DateTime, string?> onConfirm)
        {
            _original = original;
            _editingLineId = returnLine.Id;
            _onConfirm = onConfirm;

            Title = "Edit Return";
            ConfirmButtonText = "Save Return Change";
            NoticeText = "You are changing a return that was already recorded. The commission adjustment will be recalculated for the new amount.";

            Open(original, -returnLine.SalePrice, returnLine.TransactionDate.Date);
        }

        private void Open(SaleRecord original, decimal refund, DateTime date)
        {
            ProductText = $"{original.ProductName} · {original.InvoiceNumber}";
            ErrorMessage = string.Empty;
            RefundAmount = refund;
            ReturnDate = date;
            UpdatePreview();
            IsVisible = true;
        }

        partial void OnRefundAmountChanged(decimal value) => UpdatePreview();
        partial void OnReturnDateChanged(DateTime? value) => UpdatePreview();

        private DateTime EffectiveDate()
        {
            var picked = ReturnDate ?? DateTime.Today;
            // Keep the time of day for returns entered today so they sort after earlier returns
            return picked.Date == DateTime.Today ? DateTime.Now : picked.Date;
        }

        private void UpdatePreview()
        {
            if (_original == null)
            {
                return;
            }

            var preview = _payout.PreviewReturn(_original, RefundAmount, EffectiveDate(), _editingLineId);

            OriginalPriceText = preview.OriginalPrice.ToString("C");
            AlreadyRefundedText = preview.AlreadyRefunded.ToString("C");
            RemainingText = preview.Remaining.ToString("C");

            if (!preview.IsValid)
            {
                CommissionEffectText = string.Empty;
                ErrorMessage = preview.Error ?? string.Empty;
                return;
            }

            ErrorMessage = string.Empty;
            CommissionEffectText = preview.MarksSaleReturnedBeforePayout
                ? $"Full return of an unpaid sale: it will be marked Returned Before Payout and its {preview.CommissionClawback:C} commission removed."
                : $"Commission adjustment: -{preview.CommissionClawback:C}";
        }

        [RelayCommand]
        private void Confirm()
        {
            if (_original == null || _onConfirm == null)
            {
                return;
            }

            var preview = _payout.PreviewReturn(_original, RefundAmount, EffectiveDate(), _editingLineId);
            if (!preview.IsValid)
            {
                ErrorMessage = preview.Error ?? string.Empty;
                return;
            }

            var error = _onConfirm(RefundAmount, EffectiveDate());
            if (error != null)
            {
                ErrorMessage = error;
                return;
            }

            Close();
        }

        [RelayCommand]
        private void Close()
        {
            IsVisible = false;
            _original = null;
            _editingLineId = null;
            _onConfirm = null;
        }
    }
}

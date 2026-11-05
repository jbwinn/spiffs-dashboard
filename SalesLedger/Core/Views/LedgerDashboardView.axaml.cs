using Avalonia.Controls;
using Avalonia.Interactivity;
using SalesLedger.Core.ViewModels;

namespace SalesLedger.Core.Views
{
    public partial class LedgerDashboardView : UserControl
    {
        public LedgerDashboardView()
        {
            InitializeComponent();

            // When the invoice field loses focus, pull in that receipt if it already exists.
            // (Checking on every keystroke would match "INV-1" while typing "INV-12".)
            InvoiceNumberBox.LostFocus += OnInvoiceNumberLostFocus;

            // When the SKU field loses focus, warn straight away about a duplicate used/eBay SKU
            SkuBox.LostFocus += OnSkuLostFocus;
        }

        private LedgerDashboardViewModel? ViewModel => DataContext as LedgerDashboardViewModel;

        private void OnInvoiceNumberLostFocus(object? sender, RoutedEventArgs e) => ViewModel?.CheckInvoiceForExistingReceipt();

        private void OnSkuLostFocus(object? sender, RoutedEventArgs e) => ViewModel?.CheckSkuOnLeave();
    }
}

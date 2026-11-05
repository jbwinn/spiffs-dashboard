using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using SalesLedger.Core.Models;
using SalesLedger.Core.ViewModels;
using Xunit.Abstractions;

namespace SalesLedger.Tests.UiTests
{
    /// <summary>Drives the real dashboard window headlessly: real clicks, real typing.</summary>
    [Collection(HeadlessUiCollection.Name)]
    public class ReceiptDialogUiTests(HeadlessSessionFixture session, ITestOutputHelper output)
    {
        private static MainWindow ShowWindow(MainWindowViewModel vm)
        {
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            HeadlessUi.Settle();
            return window;
        }

        private static T Find<T>(Window window, string name) where T : Control =>
            window.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

        private static void Click(Window window, Control control)
        {
            var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            HeadlessUi.Settle();
        }

        private string DescribeHit(Window window, Control control)
        {
            var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
            var hit = window.InputHitTest(point) as Visual;
            var chain = hit?.GetSelfAndVisualAncestors().Take(8).Select(v => v.GetType().Name + (v is Control { Name: { } n } ? $"#{n}" : ""));
            var text = $"Hit at {point}: {string.Join(" < ", chain ?? ["(nothing)"])}";
            output.WriteLine(text);
            return text;
        }

        [Fact]
        public void AddTransaction_InvoiceField_CanBeClickedAndTypedInto()
        {
            HeadlessUi.Run(session, vm =>
            {
                var window = ShowWindow(vm);
                var dashboard = (LedgerDashboardViewModel)vm.CurrentActiveWorkspace;

                dashboard.ShowAddSaleDialogCommand.Execute(null);
                HeadlessUi.Settle();

                var invoiceBox = Find<TextBox>(window, "InvoiceNumberBox");
                output.WriteLine($"IsVisible={invoiceBox.IsEffectivelyVisible} IsEnabled={invoiceBox.IsEffectivelyEnabled} IsReadOnly={invoiceBox.IsReadOnly} Bounds={invoiceBox.Bounds}");
                DescribeHit(window, invoiceBox);

                Click(window, invoiceBox);
                output.WriteLine($"Focused after click: {window.FocusManager?.GetFocusedElement()?.GetType().Name}");

                window.KeyTextInput("INV-777");
                HeadlessUi.Settle();

                Assert.True(invoiceBox.IsFocused || invoiceBox.IsKeyboardFocusWithin, "The invoice field should take focus when clicked");
                Assert.Equal("INV-777", dashboard.InvoiceNumber);

                window.Close();
            });
        }

        private static StandardSale Seed(MainWindowViewModel vm, string invoice, string name, DateTime date,
            PayoutStatus status = PayoutStatus.Pending, string sku = "", bool used = false)
        {
            var sale = new StandardSale
            {
                InvoiceNumber = invoice, ProductName = name, Sku = sku, Category = "Lens",
                SalePrice = 1000m, TransactionDate = date, Status = status, IsUsedGear = used
            };
            vm.CommissionProc.ApplyCommission(sale, vm.LiteDb.GetUserSettings().ActiveRules ?? []);
            vm.LiteDb.Sales.Insert(sale);
            return sale;
        }

        [Fact]
        public void OpenReceipt_LoadsTheClickedProductForEditing_AndListsTheRest()
        {
            HeadlessUi.Run(session, vm =>
            {
                var date = DateTime.Today.AddDays(-3).AddHours(10);
                var productA = Seed(vm, "INV-001", "Product A", date);
                Seed(vm, "INV-001", "Product B", date);
                var window = ShowWindow(vm);
                var dashboard = (LedgerDashboardViewModel)vm.CurrentActiveWorkspace;

                dashboard.OpenReceiptCommand.Execute(productA);
                HeadlessUi.Settle();

                Assert.True(dashboard.IsSaleDialogVisible);
                Assert.Equal("INV-001", dashboard.InvoiceNumber);
                Assert.Equal("Product A", dashboard.ProductName);                       // clicked product is in the fields
                Assert.Equal(["Product B"], dashboard.DialogLines.Select(l => l.Sale.ProductName)); // the rest is listed
                Assert.StartsWith("✓ Done Editing", dashboard.AddItemButtonText);

                // "Done editing" puts A back on the list and frees the fields for another product
                dashboard.AddDialogItemCommand.Execute(null);
                HeadlessUi.Settle();
                Assert.Equal(2, dashboard.DialogLines.Count);
                Assert.Equal(string.Empty, dashboard.ProductName);
                Assert.Equal("+ Add Product to Receipt", dashboard.AddItemButtonText);
                Assert.False(dashboard.IsConfirmVisible); // existing item: no date question

                window.Close();
            });
        }

        [Fact]
        public void OpenReceipt_OnAPaidProduct_ListsEverything_AndLeavesTheFieldsEmpty()
        {
            HeadlessUi.Run(session, vm =>
            {
                var paid = Seed(vm, "INV-002", "Paid Lens", DateTime.Today.AddDays(-40), PayoutStatus.Paid);
                var window = ShowWindow(vm);
                var dashboard = (LedgerDashboardViewModel)vm.CurrentActiveWorkspace;

                dashboard.OpenReceiptCommand.Execute(paid);
                HeadlessUi.Settle();

                Assert.Equal(string.Empty, dashboard.ProductName);
                var line = Assert.Single(dashboard.DialogLines);
                Assert.True(line.IsReadOnly);
                Assert.True(dashboard.IsInvoiceLocked);
                Assert.Contains("paid", dashboard.ReceiptNotice);

                window.Close();
            });
        }

        [Fact]
        public void AddingAProductToAnOlderReceipt_AsksWhichDate_AndRespectsTheChoice()
        {
            HeadlessUi.Run(session, vm =>
            {
                var receiptDate = DateTime.Today.AddDays(-10).AddHours(11);
                var original = Seed(vm, "INV-003", "Original Lens", receiptDate, PayoutStatus.Paid);
                var window = ShowWindow(vm);
                var dashboard = (LedgerDashboardViewModel)vm.CurrentActiveWorkspace;

                dashboard.OpenReceiptCommand.Execute(original);
                dashboard.ProductName = "Added Later";
                dashboard.SalePrice = 250m;
                dashboard.IsUsedGear = false;
                dashboard.AddDialogItemCommand.Execute(null);
                HeadlessUi.Settle();

                Assert.True(dashboard.IsConfirmVisible);
                Assert.Contains("Date", dashboard.ConfirmTitle);
                Assert.StartsWith("Today", dashboard.ConfirmSecondaryText);

                dashboard.ConfirmSecondaryCommand.Execute(null); // use today's date
                HeadlessUi.Settle();

                var added = dashboard.DialogLines.Single(l => l.Sale.ProductName == "Added Later");
                Assert.True(added.HasDateNote);

                dashboard.SaveSaleCommand.Execute(null);
                HeadlessUi.Settle();

                Assert.False(dashboard.IsSaleDialogVisible);
                var saved = vm.LiteDb.Sales.FindAll().Single(s => s.ProductName == "Added Later");
                Assert.Equal(DateTime.Today, saved.TransactionDate.Date);
                Assert.Equal("INV-003", saved.InvoiceNumber);
                Assert.Equal(receiptDate, vm.LiteDb.Sales.FindById(original.Id).TransactionDate); // paid item untouched

                window.Close();
            });
        }

        [Fact]
        public void UsedSkuAlreadySold_ShowsAWarning_AndGoBackDoesNotAdd()
        {
            HeadlessUi.Run(session, vm =>
            {
                Seed(vm, "INV-004", "Used Body", DateTime.Today.AddDays(-5), PayoutStatus.Paid, sku: "U-4471", used: true);
                var window = ShowWindow(vm);
                var dashboard = (LedgerDashboardViewModel)vm.CurrentActiveWorkspace;

                dashboard.ShowAddSaleDialogCommand.Execute(null);
                dashboard.InvoiceNumber = "INV-005";
                dashboard.Sku = "u-4471";
                dashboard.ProductName = "Used Body";
                dashboard.IsUsedGear = true;
                dashboard.AddDialogItemCommand.Execute(null);
                HeadlessUi.Settle();

                Assert.True(dashboard.IsConfirmVisible);
                Assert.Contains("INV-004", dashboard.ConfirmMessage);

                dashboard.ConfirmCancelCommand.Execute(null);     // Go Back
                Assert.Empty(dashboard.DialogLines);
                Assert.Equal("Used Body", dashboard.ProductName); // fields kept so it can be corrected

                dashboard.AddDialogItemCommand.Execute(null);
                dashboard.ConfirmPrimaryCommand.Execute(null);    // Add Anyway
                Assert.Single(dashboard.DialogLines);

                window.Close();
            });
        }
    }
}

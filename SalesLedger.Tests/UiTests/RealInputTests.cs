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
    /// <summary>Receipt-entry behaviour driven only by real clicks and typing in the actual dialog.</summary>
    [Collection(HeadlessUiCollection.Name)]
    public class RealInputTests(HeadlessSessionFixture session, ITestOutputHelper output)
    {
        private static T Find<T>(Window window, string name) where T : Control =>
            window.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

        private static void Click(Window window, Control control)
        {
            var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            HeadlessUi.Settle();
        }

        private static void ClickAndType(Window window, Control control, string text)
        {
            Click(window, control);
            window.KeyTextInput(text);
            HeadlessUi.Settle();
        }

        private void Log(LedgerDashboardViewModel d, string step) =>
            output.WriteLine($"[{step}] invoice='{d.InvoiceNumber}' date={d.TransactionDate:yyyy-MM-dd} lines={d.DialogLines.Count} " +
                             $"confirm={d.IsConfirmVisible}:'{d.ConfirmTitle}' error='{d.DialogErrorMessage}'");

        private static T Seed<T>(MainWindowViewModel vm, T sale, string invoice, string sku, DateTime date, PayoutStatus status = PayoutStatus.Pending)
            where T : SaleRecord
        {
            sale.InvoiceNumber = invoice;
            sale.Sku = sku;
            sale.ProductName = "Seeded " + sku;
            sale.Category = "Lens";
            sale.SalePrice = 1000m;
            sale.TransactionDate = date;
            sale.Status = status;
            vm.CommissionProc.ApplyCommission(sale, vm.LiteDb.GetUserSettings().ActiveRules ?? []);
            vm.LiteDb.Sales.Insert(sale);
            return sale;
        }

        private static (MainWindow Window, LedgerDashboardViewModel Dashboard) OpenAddDialog(MainWindowViewModel vm)
        {
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();
            HeadlessUi.Settle();
            var dashboard = (LedgerDashboardViewModel)vm.CurrentActiveWorkspace;
            dashboard.Refresh();
            dashboard.ShowAddSaleDialogCommand.Execute(null);
            HeadlessUi.Settle();
            return (window, dashboard);
        }

        private static void FillProduct(Window window, string sku, string name, string price)
        {
            ClickAndType(window, Find<TextBox>(window, "SkuBox"), sku);
            ClickAndType(window, Find<AutoCompleteBox>(window, "ProductNameBox"), name);
            ClickAndType(window, Find<NumericUpDown>(window, "SalePriceBox"), price);
        }

        [Fact]
        public void PriceField_TypingReplacesTheValue()
        {
            HeadlessUi.Run(session, vm =>
            {
                var (window, d) = OpenAddDialog(vm);

                ClickAndType(window, Find<NumericUpDown>(window, "SalePriceBox"), "250");
                Click(window, Find<TextBox>(window, "SkuBox")); // leave the field so the value commits

                Assert.Equal(250m, d.SalePrice); // not $0.00250
            });
        }

        [Fact]
        public void ExistingInvoiceOnNewTransaction_LoadsTheReceipt_AsksAboutTheDate_AndNeverMovesTheOriginal()
        {
            HeadlessUi.Run(session, vm =>
            {
                var originalDate = DateTime.Today.AddDays(-15).AddHours(10);
                var original = Seed(vm, new StandardSale(), "INV-OLD", "NEW-0", originalDate);
                var (window, d) = OpenAddDialog(vm);

                ClickAndType(window, Find<TextBox>(window, "InvoiceNumberBox"), "INV-OLD");
                FillProduct(window, "NEW-1", "Second Item", "250");
                Log(d, "filled");
                Assert.Contains("already exists", d.ReceiptNotice);
                Assert.Contains("INV-OLD", d.DialogTitle);

                Click(window, Find<Button>(window, "SaveEntryButton"));
                Log(d, "save");
                Assert.True(d.IsConfirmVisible);
                Assert.Contains("Date", d.ConfirmTitle);

                d.ConfirmPrimaryCommand.Execute(null); // receipt date
                HeadlessUi.Settle();

                Assert.False(d.IsSaleDialogVisible);
                Assert.Equal(originalDate, vm.LiteDb.Sales.FindById(original.Id).TransactionDate);
                var added = vm.LiteDb.Sales.FindAll().Single(s => s.ProductName == "Second Item");
                Assert.Equal(originalDate.Date, added.TransactionDate.Date);
                Assert.Equal(250m, added.SalePrice);
            });
        }

        [Fact]
        public void ChangingTheDateOfAnExistingReceipt_AsksBeforeMovingSavedItems()
        {
            HeadlessUi.Run(session, vm =>
            {
                var originalDate = DateTime.Today.AddDays(-15).AddHours(10);
                var original = Seed(vm, new StandardSale(), "INV-OLD", "NEW-0", originalDate);
                var (window, d) = OpenAddDialog(vm);

                ClickAndType(window, Find<TextBox>(window, "InvoiceNumberBox"), "INV-OLD");
                FillProduct(window, "NEW-1", "Second Item", "250");
                d.TransactionDate = DateTime.Today;   // the user changes the date field back to today
                HeadlessUi.Settle();

                Click(window, Find<Button>(window, "SaveEntryButton"));
                Log(d, "save");
                Assert.True(d.IsConfirmVisible);
                Assert.Contains("Existing Items", d.ConfirmTitle);

                d.ConfirmPrimaryCommand.Execute(null); // keep the original date
                HeadlessUi.Settle();

                Assert.False(d.IsSaleDialogVisible);
                Assert.Equal(originalDate, vm.LiteDb.Sales.FindById(original.Id).TransactionDate);
                Assert.Equal(DateTime.Today, vm.LiteDb.Sales.FindAll().Single(s => s.ProductName == "Second Item").TransactionDate.Date);
            });
        }

        [Fact]
        public void ChangingTheDate_AndChoosingMove_MovesTheSavedItems()
        {
            HeadlessUi.Run(session, vm =>
            {
                var original = Seed(vm, new StandardSale(), "INV-OLD", "NEW-0", DateTime.Today.AddDays(-15).AddHours(10));
                var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
                window.Show();
                var d = (LedgerDashboardViewModel)vm.CurrentActiveWorkspace;

                d.OpenReceiptCommand.Execute(original);
                d.AddDialogItemCommand.Execute(null);  // done editing the clicked item
                d.TransactionDate = DateTime.Today;
                d.SaveSaleCommand.Execute(null);
                Assert.True(d.IsConfirmVisible);

                d.ConfirmSecondaryCommand.Execute(null); // move to today
                HeadlessUi.Settle();

                Assert.Equal(DateTime.Today, vm.LiteDb.Sales.FindById(original.Id).TransactionDate.Date);
            });
        }

        [Fact]
        public void DuplicateSku_OfAnEarlierNewGearSale_WarnsWhenLeavingTheSkuField()
        {
            HeadlessUi.Run(session, vm =>
            {
                // The reported case: the earlier sale is pending, on another invoice, and was entered as new gear
                Seed(vm, new StandardSale { IsUsedGear = false }, "INV-A", "SONY-A7IV", DateTime.Today.AddDays(-3));
                var (window, d) = OpenAddDialog(vm);

                ClickAndType(window, Find<TextBox>(window, "InvoiceNumberBox"), "INV-B");
                ClickAndType(window, Find<TextBox>(window, "SkuBox"), "SONY-A7IV");
                Click(window, Find<AutoCompleteBox>(window, "ProductNameBox")); // leave the SKU field
                Log(d, "left sku");

                Assert.True(d.IsConfirmVisible);
                Assert.Equal("Duplicate SKU", d.ConfirmTitle);
                Assert.Contains("INV-A", d.ConfirmMessage);

                d.ConfirmPrimaryCommand.Execute(null); // keep this SKU
                ClickAndType(window, Find<AutoCompleteBox>(window, "ProductNameBox"), "Sony A7 IV (Used)");
                ClickAndType(window, Find<NumericUpDown>(window, "SalePriceBox"), "1800");
                Click(window, Find<Button>(window, "AddItemButton"));

                Assert.False(d.IsConfirmVisible); // confirmed once, not asked again
                Assert.Single(d.DialogLines);
            });
        }

        [Fact]
        public void ReturnedItemSoldAgain_ShowsAResaleNotice()
        {
            HeadlessUi.Run(session, vm =>
            {
                var earlier = Seed(vm, new StandardSale { IsUsedGear = true }, "INV-A", "U-200", DateTime.Today.AddDays(-20), PayoutStatus.Paid);
                vm.PayoutService.ProcessReturn(earlier.Id);   // returned after payout
                var (window, d) = OpenAddDialog(vm);

                ClickAndType(window, Find<TextBox>(window, "SkuBox"), "U-200");
                Click(window, Find<AutoCompleteBox>(window, "ProductNameBox"));

                Assert.True(d.IsConfirmVisible);
                Assert.Equal("Returned Item Being Sold Again", d.ConfirmTitle);
                Assert.StartsWith("Yes", d.ConfirmPrimaryText);
            });
        }

        [Fact]
        public void EbayEntries_AreCheckedEvenWhenNotMarkedUsed()
        {
            HeadlessUi.Run(session, vm =>
            {
                Seed(vm, new EbaySale { IsUsedGear = false }, "EBAY-1", "E-77", DateTime.Today.AddDays(-2));
                var (window, d) = OpenAddDialog(vm);

                d.SelectedRecordTypeIndex = 2; // eBay
                d.IsUsedGear = false;
                HeadlessUi.Settle();
                ClickAndType(window, Find<TextBox>(window, "SkuBox"), "E-77");
                Click(window, Find<AutoCompleteBox>(window, "ProductNameBox"));

                Assert.True(d.IsConfirmVisible);
                Assert.Equal("Duplicate SKU", d.ConfirmTitle);
            });
        }

        [Fact]
        public void NewGearEntries_AreNotChecked()
        {
            HeadlessUi.Run(session, vm =>
            {
                Seed(vm, new StandardSale { IsUsedGear = false }, "INV-A", "FUJI-XT5", DateTime.Today.AddDays(-2));
                var (window, d) = OpenAddDialog(vm);

                d.IsUsedGear = false; // a second new X-T5 shares the SKU legitimately
                ClickAndType(window, Find<TextBox>(window, "SkuBox"), "FUJI-XT5");
                Click(window, Find<AutoCompleteBox>(window, "ProductNameBox"));

                Assert.False(d.IsConfirmVisible);
            });
        }
    }
}

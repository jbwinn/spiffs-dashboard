using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Collections;
using SalesLedger.Core.Models;
using SalesLedger.Core.Services;

namespace SalesLedger.Core.ViewModels
{
    public partial class LedgerDashboardViewModel : ObservableObject
    {
        private readonly MainWindowViewModel _mainVm;

        // KPI Properties. Sales are gross (everything sold, including items later returned); returns and net are shown alongside.
        [ObservableProperty] public partial decimal GrossSales { get; set; }
        [ObservableProperty] public partial decimal ReturnsAmount { get; set; }
        [ObservableProperty] public partial decimal NetSales { get; set; }
        [ObservableProperty] public partial int ReturnCount { get; set; }
        [ObservableProperty] public partial string SalesSubText { get; set; } = string.Empty;
        [ObservableProperty] public partial int TotalUnitsSold { get; set; }   // net: less items fully returned
        [ObservableProperty] public partial int GrossUnitsSold { get; set; }
        [ObservableProperty] public partial int UnitsReturned { get; set; }
        [ObservableProperty] public partial string UnitsSubText { get; set; } = string.Empty;
        [ObservableProperty] public partial decimal TotalCommission { get; set; }
        [ObservableProperty] public partial decimal AverageSalePrice { get; set; }

        // Timeframe & Metric selections
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsCustomTimeframe))]
        public partial string SelectedTimeframe { get; set; } = "Last 30 Days";

        [ObservableProperty] public partial string SelectedMetric { get; set; } = "Revenue"; // "Revenue", "Quantity", "Commission"
        [ObservableProperty] public partial bool HighValueSalesOnly { get; set; }

        // Custom timeframe range (used when SelectedTimeframe is "Custom")
        [ObservableProperty] public partial DateTime? ChartStartDate { get; set; } = DateTime.Today.AddDays(-30);
        [ObservableProperty] public partial DateTime? ChartEndDate { get; set; } = DateTime.Today;
        public bool IsCustomTimeframe => SelectedTimeframe == AnalyticsService.Custom;

        /// <summary>The dates the dashboard is showing, e.g. "Oct 4, 2026 – Nov 3, 2026".</summary>
        [ObservableProperty] public partial string RangeDescription { get; set; } = string.Empty;

        // Collections
        public List<string> TimeframeOptions { get; } =
        [
            AnalyticsService.CurrentPayPeriod, "Current Month", "Last Month", "Last 30 Days", "Last 3 Months", "Last 6 Months", "Year to Date", AnalyticsService.Custom
        ];

        public List<string> MetricOptions { get; } = [ "Revenue", "Quantity", "Commission" ];

        private ObservableCollection<SaleRecord> RecentSales { get; } = [];
        public DataGridCollectionView RecentSalesView { get; }
        public ObservableCollection<ChartItem> ChartItems { get; } = [];

        // Ledger Search & Filter Panel Properties
        [ObservableProperty] public partial string SearchText { get; set; } = string.Empty;
        [ObservableProperty] public partial string SelectedSaleTypeFilter { get; set; } = "All";
        [ObservableProperty] public partial string SelectedCategoryFilter { get; set; } = "All";
        [ObservableProperty] public partial string SelectedDateFilterType { get; set; } = "All Time";
        [ObservableProperty] public partial DateTime? CustomStartDate { get; set; } = DateTime.Now.AddDays(-30);
        [ObservableProperty] public partial DateTime? CustomEndDate { get; set; } = DateTime.Now;

        public bool IsCustomDateFilter => SelectedDateFilterType == "Custom";

        public List<string> SaleTypeFilterOptions { get; } =
        [
            "All", "Standard (New)", "Standard (Used)", "eBay", "Warranty", "Returns"
        ];

        public List<string> DateFilterTypeOptions { get; } =
        [
            "All Time", AnalyticsService.CurrentPayPeriod, "Current Month", "Last Month", "Last 30 Days", "Last 3 Months", "Last 6 Months", "Year to Date", "Custom"
        ];

        public ObservableCollection<string> CategoryFilterOptions { get; } = [ "All" ];

        // Category Breakdown Aggregations
        [ObservableProperty] public partial string CategoryBreakdownMetric { get; set; } = "Dollar Amount"; // "Dollar Amount" or "Quantity"
        public ObservableCollection<CategoryBreakdownItem> CategoryBreakdownItems { get; } = [];

        public List<string> CategoryBreakdownMetricOptions { get; } = [ "Dollar Amount", "Quantity" ];

        private static readonly string[] CategoryColors =
        [
            "#4F759B", // CS-Blue
            "#A5CC6B", // CS-Green
            "#F5C44C", // CS-Yellow
            "#5D4532", // CS-Brown
            "#7291AF", // Blues Step 1
            "#B7D689", // Greens Step 1
            "#F7D070", // Yellows Step 1
            "#7D6A5B", // Browns Step 1
            "#5C5958", // Grays Step 1
            "#95ACC3", // Blues Step 2
            "#705E78"  // Slate Plum (Brand complement)
        ];

        // Last trend query, reused when only the category breakdown metric changes
        private List<TrendBucket> _lastTrends = [];

        // Sub-dialogs
        public CsvImportViewModel Import { get; }
        public ReturnDialogViewModel ReturnDialog { get; }

        // Selected Row context actions
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(OpenReceiptCommand))]
        [NotifyCanExecuteChangedFor(nameof(ProcessReturnCommand))]
        public partial SaleRecord? SelectedSale { get; set; }

        // Ledger-level error banner (e.g. a delete that isn't allowed)
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasLedgerMessage))]
        public partial string LedgerMessage { get; set; } = string.Empty;

        public bool HasLedgerMessage => !string.IsNullOrEmpty(LedgerMessage);

        // Receipt dialog state
        [ObservableProperty] public partial bool IsSaleDialogVisible { get; set; }
        [ObservableProperty] public partial string DialogTitle { get; set; } = "Add New Transaction";
        [ObservableProperty] private partial bool IsEditing { get; set; }
        [ObservableProperty] public partial bool IsRestoreReturnDialogVisible { get; set; }
        public SaleRecord? SelectedSaleToRestore { get; set; }

        // The receipt line currently loaded into the product fields (null = entering a new product)
        private ReceiptLineViewModel? _editingLine;
        private readonly HashSet<Guid> _removedItemIds = [];
        private readonly Dictionary<Guid, StagedReturn> _stagedReturns = [];

        // Existing-receipt context: the receipt's date when it was opened/loaded (null = brand-new receipt),
        // the invoice that was loaded by typing its number, and the date choice for products added to it
        private DateTime? _receiptDate;
        private string? _loadedInvoice;
        private DateTime? _dateBeforeLoad;
        private bool? _newItemsUseToday;

        /// <summary>Explains what's in the dialog, e.g. "INV-123 already has 2 items..."</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasReceiptNotice))]
        public partial string ReceiptNotice { get; set; } = string.Empty;

        public bool HasReceiptNotice => !string.IsNullOrEmpty(ReceiptNotice);

        // Generic confirmation popup (used-SKU warning, date choice for added products)
        private Action? _confirmPrimaryAction;
        private Action? _confirmSecondaryAction;

        [ObservableProperty] public partial bool IsConfirmVisible { get; set; }
        [ObservableProperty] public partial string ConfirmTitle { get; set; } = string.Empty;
        [ObservableProperty] public partial string ConfirmMessage { get; set; } = string.Empty;
        [ObservableProperty] public partial string ConfirmPrimaryText { get; set; } = "OK";
        [ObservableProperty] public partial string ConfirmCancelText { get; set; } = "Cancel";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasConfirmSecondary))]
        public partial string ConfirmSecondaryText { get; set; } = string.Empty;

        public bool HasConfirmSecondary => !string.IsNullOrEmpty(ConfirmSecondaryText);

        // Product name autofill, built from names already in the ledger
        private List<ProductSuggestion> _productSuggestions = [];
        private bool _suppressSuggestionFill;

        [ObservableProperty] public partial List<string> ProductNameSuggestions { get; private set; } = [];
        [ObservableProperty] public partial string? SelectedProductSuggestion { get; set; }

        public ObservableCollection<ReceiptLineViewModel> DialogLines { get; } = [];
        public bool HasDialogSales => DialogLines.Count > 0;

        /// <summary>The invoice number can't change once part of the receipt is locked, or the receipt would split.</summary>
        [ObservableProperty] public partial bool IsInvoiceLocked { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasDialogError))]
        public partial string DialogErrorMessage { get; set; } = string.Empty;

        public bool HasDialogError => !string.IsNullOrEmpty(DialogErrorMessage);

        [ObservableProperty] public partial string InvoiceNumber { get; set; } = string.Empty;
        [ObservableProperty] public partial string Sku { get; set; } = string.Empty;
        [ObservableProperty] public partial string ProductName { get; set; } = string.Empty;
        [ObservableProperty] public partial string Category { get; set; } = string.Empty;
        [ObservableProperty] public partial decimal SalePrice { get; set; }
        [ObservableProperty] public partial DateTime? TransactionDate { get; set; } = DateTime.Now;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SelectedRecordTypeIndex))]
        [NotifyPropertyChangedFor(nameof(InvoiceLabel))]
        [NotifyPropertyChangedFor(nameof(InvoicePlaceholder))]
        [NotifyPropertyChangedFor(nameof(ShowCategory))]
        [NotifyPropertyChangedFor(nameof(ShowUsedGear))]
        [NotifyPropertyChangedFor(nameof(ShowWarranty))]
        private partial SaleType RecordType { get; set; } = SaleType.Standard;

        public int SelectedRecordTypeIndex
        {
            get => RecordType == SaleType.Standard ? 0 : (RecordType == SaleType.Warranty ? 1 : 2);
            set => RecordType = value == 0 ? SaleType.Standard : (value == 1 ? SaleType.Warranty : SaleType.Ebay);
        }

        public string InvoiceLabel => RecordType == SaleType.Ebay ? "Order Code" : "Invoice Number";
        public string InvoicePlaceholder => RecordType == SaleType.Ebay ? "e.g. EBAY-1001" : "e.g. INV-1001";

        public bool ShowCategory => RecordType == SaleType.Standard || RecordType == SaleType.Ebay;
        public bool ShowUsedGear => RecordType == SaleType.Standard || RecordType == SaleType.Ebay;
        public bool ShowWarranty => RecordType == SaleType.Warranty;

        // Standard sale specific
        [ObservableProperty] public partial bool IsUsedGear { get; set; }

        // Warranty sale specific
        [ObservableProperty] public partial string WarrantyTypeName { get; set; } = string.Empty;
        [ObservableProperty] public partial decimal ManufacturerPrice { get; set; }

        // Dropdown options (cached; refreshed on every LoadData)
        [ObservableProperty] public partial List<string> Categories { get; private set; } = [];
        [ObservableProperty] public partial List<string> WarrantyTypes { get; private set; } = [];

        public LedgerDashboardViewModel(MainWindowViewModel mainVm)
        {
            _mainVm = mainVm ?? throw new ArgumentNullException(nameof(mainVm));
            Import = new CsvImportViewModel(_mainVm.LiteDb, _mainVm.CsvImport, _mainVm.SkuCheck, LoadData);
            ReturnDialog = new ReturnDialogViewModel(_mainVm.PayoutService);

            RecentSalesView = new DataGridCollectionView(RecentSales);
            RecentSalesView.SortDescriptions.Add(DataGridSortDescription.FromPath("TransactionDate", System.ComponentModel.ListSortDirection.Descending));
            LoadData();
        }

        /// <summary>Reloads KPIs, charts, dropdowns and the ledger grid.</summary>
        public void Refresh() => LoadData();

        private void LoadData()
        {
            RefreshTaxonomy();

            var range = _mainVm.Analytics.ResolveRange(SelectedTimeframe, ChartStartDate, ChartEndDate);
            RangeDescription = range.CurrentPayPeriodOnly
                ? $"Unpaid sales and returns (not yet finalized), dated {range.Start:MMM d, yyyy} – {range.End:MMM d, yyyy}"
                : $"{range.Start:MMM d, yyyy} – {range.End:MMM d, yyyy}";

            // Load KPIs
            var summary = _mainVm.Analytics.GetSummary(range);
            GrossSales = summary.GrossSales;
            ReturnsAmount = summary.Returns;
            NetSales = summary.NetSales;
            ReturnCount = summary.ReturnCount;
            SalesSubText = summary.Returns != 0
                ? $"−{summary.Returns:C} returns · {summary.NetSales:C} net"
                : "No returns in this range";
            TotalUnitsSold = summary.TotalUnitsSold;
            GrossUnitsSold = summary.GrossUnitsSold;
            UnitsReturned = summary.UnitsReturned;
            UnitsSubText = summary.UnitsReturned != 0
                ? $"{summary.GrossUnitsSold} sold · {summary.UnitsReturned} returned"
                : "No items returned";
            TotalCommission = summary.TotalCommission;
            AverageSalePrice = summary.AverageSalePrice;

            // Load Trends
            _lastTrends = _mainVm.Analytics.GetTrends(range);
            ChartItems.Clear();

            double maxVal = 1.0;
            var items = new List<(TrendBucket Bucket, double PosVal, double NegVal)>();

            foreach (var bucket in _lastTrends)
            {
                double posVal = SelectedMetric switch
                {
                    "Revenue" => bucket.TotalRevenue,
                    "Quantity" => bucket.TotalQuantity,
                    "Commission" => bucket.TotalCommission,
                    _ => bucket.TotalRevenue
                };

                double negVal = SelectedMetric switch
                {
                    "Revenue" => Math.Abs(bucket.ReturnOffsetMetric.Revenue),
                    "Quantity" => Math.Abs(bucket.ReturnOffsetMetric.Quantity),
                    "Commission" => Math.Abs(bucket.ReturnOffsetMetric.Commission),
                    _ => Math.Abs(bucket.ReturnOffsetMetric.Revenue)
                };

                items.Add((bucket, posVal, negVal));
                if (posVal > maxVal) maxVal = posVal;
                if (negVal > maxVal) maxVal = negVal;
            }

            foreach (var item in items)
            {
                double posH = 0;
                double negH = 0;

                if (maxVal > 0)
                {
                    posH = (item.PosVal / maxVal) * 130; // max positive height = 130px
                    if (posH < 4 && item.PosVal > 0) posH = 4;

                    negH = (item.NegVal / maxVal) * 130; // max negative height = 130px
                    if (negH < 4 && item.NegVal > 0) negH = 4;
                }

                var chartItem = new ChartItem(item.Bucket, posH, negH, SelectedMetric);
                ChartItems.Add(chartItem);
            }

            // Update category options
            UpdateCategoryFilterOptions();

            // Load Category Breakdown Chart
            UpdateCategoryBreakdown();

            // Load Sales list
            LoadSalesList();
        }

        private void RefreshTaxonomy()
        {
            var settings = _mainVm.LiteDb.GetUserSettings();
            Categories = (settings.ProductCategories ?? []).Where(c => c.IsActive).Select(c => c.Name).ToList();
            WarrantyTypes = (settings.WarrantyTypes ?? []).Where(w => w.IsActive).Select(w => w.Name).ToList();
        }

        private void UpdateCategoryFilterOptions()
        {
            var selected = SelectedCategoryFilter;
            CategoryFilterOptions.Clear();
            CategoryFilterOptions.Add("All");
            foreach (var cat in Categories)
            {
                CategoryFilterOptions.Add(cat);
            }
            SelectedCategoryFilter = CategoryFilterOptions.Contains(selected) ? selected : "All";
        }

        private void UpdateCategoryBreakdown()
        {
            var totals = new Dictionary<string, double>();
            double overallTotal = 0;

            foreach (var bucket in _lastTrends)
            {
                foreach (var kvp in bucket.CategoryBreakdown)
                {
                    double val = CategoryBreakdownMetric == "Dollar Amount" ? kvp.Value.Revenue : kvp.Value.Quantity;
                    totals.TryAdd(kvp.Key, 0);
                    totals[kvp.Key] += val;
                    overallTotal += val;
                }
            }

            CategoryBreakdownItems.Clear();
            int colorIndex = 0;
            foreach (var kvp in totals.Where(x => x.Value > 0).OrderByDescending(x => x.Value))
            {
                double pct = overallTotal > 0 ? (kvp.Value / overallTotal) * 100 : 0;
                string valText = CategoryBreakdownMetric == "Dollar Amount"
                    ? kvp.Value.ToString("C0")
                    : $"{kvp.Value} units";

                var color = CategoryColors[colorIndex % CategoryColors.Length];
                colorIndex++;

                CategoryBreakdownItems.Add(new CategoryBreakdownItem
                {
                    Category = kvp.Key,
                    ValueText = valText,
                    Percentage = pct,
                    Color = color
                });
            }
        }

        private void LoadSalesList()
        {
            DateTime start = DateTime.MinValue;
            DateTime end = DateTime.MaxValue;

            bool currentPayPeriod = SelectedDateFilterType == AnalyticsService.CurrentPayPeriod;

            if (SelectedDateFilterType == "Custom" && CustomStartDate.HasValue && CustomEndDate.HasValue)
            {
                start = CustomStartDate.Value.Date;
                end = CustomEndDate.Value.Date.AddDays(1).AddMilliseconds(-1);
            }
            else if (SelectedDateFilterType != "All Time" && !currentPayPeriod)
            {
                var range = _mainVm.Analytics.GetDateRangeAndScale(SelectedDateFilterType);
                start = range.Start;
                end = range.End;
            }

            // Fetch from LiteDB for display accuracy and direct edit hookups
            var query = _mainVm.LiteDb.Sales.Find(x => x.TransactionDate >= start && x.TransactionDate <= end);

            if (currentPayPeriod)
            {
                // Exactly what the next period close would include, whatever the dates
                query = query.Where(x => x.Status == PayoutStatus.Pending ||
                                         (x.Status == PayoutStatus.ReturnedBeforePayout && x.AssociatedReportId == null));
            }

            if (HighValueSalesOnly)
            {
                query = query.Where(x => x.SalePrice >= 1500m);
            }

            if (SelectedCategoryFilter != "All" && !string.IsNullOrEmpty(SelectedCategoryFilter))
            {
                query = query.Where(x => x.Category == SelectedCategoryFilter);
            }

            if (SelectedSaleTypeFilter != "All" && !string.IsNullOrEmpty(SelectedSaleTypeFilter))
            {
                query = SelectedSaleTypeFilter switch
                {
                    "Standard (New)" => query.Where(x => !x.IsReturn && x is StandardSale { IsUsedGear: false }),
                    "Standard (Used)" => query.Where(x => !x.IsReturn && x is StandardSale { IsUsedGear: true }),
                    "eBay" => query.Where(x => !x.IsReturn && x.RecordType == SaleType.Ebay),
                    "Warranty" => query.Where(x => !x.IsReturn && x.RecordType == SaleType.Warranty),
                    // Return lines, sales returned before payout, and legacy ReturnOffsetSale records
                    "Returns" => query.Where(x => x.IsReturn || x.RecordType == SaleType.ReturnOffset || x.Status == PayoutStatus.ReturnedBeforePayout),
                    _ => query
                };
            }

            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                string search = SearchText.Trim();
                query = query.Where(x =>
                    x.InvoiceNumber.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    x.ProductName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    x.Sku.Contains(search, StringComparison.OrdinalIgnoreCase)
                );
            }

            RecentSales.Clear();
            foreach (var sale in query.OrderByDescending(s => s.TransactionDate))
            {
                RecentSales.Add(sale);
            }
        }

        partial void OnSelectedTimeframeChanged(string value) { _ = value; LoadData(); }
        partial void OnChartStartDateChanged(DateTime? value) { _ = value; if (IsCustomTimeframe) LoadData(); }
        partial void OnChartEndDateChanged(DateTime? value) { _ = value; if (IsCustomTimeframe) LoadData(); }
        partial void OnSelectedMetricChanged(string value) { _ = value; LoadData(); }
        partial void OnCategoryBreakdownMetricChanged(string value) { _ = value; UpdateCategoryBreakdown(); }
        partial void OnHighValueSalesOnlyChanged(bool value) { _ = value; LoadSalesList(); }
        partial void OnSearchTextChanged(string value) { _ = value; LoadSalesList(); }
        partial void OnSelectedSaleTypeFilterChanged(string value) { _ = value; LoadSalesList(); }
        partial void OnSelectedCategoryFilterChanged(string value) { _ = value; LoadSalesList(); }
        partial void OnSelectedDateFilterTypeChanged(string value)
        {
            _ = value;
            OnPropertyChanged(nameof(IsCustomDateFilter));
            LoadSalesList();
        }
        partial void OnCustomStartDateChanged(DateTime? value) { _ = value; LoadSalesList(); }
        partial void OnCustomEndDateChanged(DateTime? value) { _ = value; LoadSalesList(); }

        [RelayCommand]
        private void ResetFilters()
        {
            SearchText = string.Empty;
            SelectedSaleTypeFilter = "All";
            SelectedCategoryFilter = "All";
            SelectedDateFilterType = "All Time";
            HighValueSalesOnly = false;
            CustomStartDate = DateTime.Now.AddDays(-30);
            CustomEndDate = DateTime.Now;
        }

        public void OnSyncCompleted()
        {
            // The refresh runs later on the UI thread; by then the app may have shut down (databases closed)
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (!_mainVm.IsDisposed)
                {
                    LoadData();
                }
            });
        }

        [RelayCommand]
        private void DismissLedgerMessage() => LedgerMessage = string.Empty;

        // ------------------------------------------------------------------
        // Receipt dialog: add a new receipt, or open an existing one
        // ------------------------------------------------------------------

        private void ResetReceiptDialog()
        {
            DialogLines.Clear();
            _removedItemIds.Clear();
            _stagedReturns.Clear();
            SetEditingLine(null);
            _receiptDate = null;
            _loadedInvoice = null;
            _dateBeforeLoad = null;
            _newItemsUseToday = null;
            IsInvoiceLocked = false;
            DialogErrorMessage = string.Empty;
            ReceiptNotice = string.Empty;
            IsConfirmVisible = false;
            OnPropertyChanged(nameof(HasDialogSales));
            RefreshProductSuggestions();
        }

        private void ClearProductFields()
        {
            _suppressSuggestionFill = true;
            Sku = string.Empty;
            ProductName = string.Empty;
            SalePrice = 0m;
            ManufacturerPrice = 0m;
            SelectedProductSuggestion = null;
            _suppressSuggestionFill = false;
            SetEditingLine(null);
        }

        private void ResetNewProductDefaults(SaleType type)
        {
            ClearProductFields();
            RecordType = type is SaleType.Warranty or SaleType.Ebay ? type : SaleType.Standard;
            Category = Categories.FirstOrDefault() ?? string.Empty;
            IsUsedGear = true;
            WarrantyTypeName = WarrantyTypes.FirstOrDefault() ?? string.Empty;
        }

        [RelayCommand]
        private void ShowAddSaleDialog()
        {
            DialogTitle = "Add New Transaction";
            IsEditing = false;
            ResetReceiptDialog();

            InvoiceNumber = string.Empty;
            TransactionDate = DateTime.Now;
            ResetNewProductDefaults(SaleType.Standard);

            IsSaleDialogVisible = true;
        }

        /// <summary>
        /// Opens a receipt from the ledger: every item goes in the list (pending ones editable, the rest read-only)
        /// and the product fields start empty, ready to add another product.
        /// </summary>
        [RelayCommand]
        private void OpenReceipt(SaleRecord? parameter)
        {
            var target = parameter ?? SelectedSale;
            if (target == null || !target.CanOpenReceipt) return;

            var receipt = _mainVm.Receipts.LoadReceipt(target.InvoiceNumber);

            IsEditing = true;
            ResetReceiptDialog();

            DialogTitle = $"Receipt {receipt.InvoiceNumber}";
            InvoiceNumber = receipt.InvoiceNumber;
            _receiptDate = receipt.ReceiptDate ?? target.TransactionDate;
            TransactionDate = _receiptDate;
            _loadedInvoice = receipt.InvoiceNumber;

            AddReceiptLines(receipt);
            IsInvoiceLocked = receipt.ReadOnlyItems.Count > 0;

            ResetNewProductDefaults(target.RecordType);

            // The clicked product opens for editing (if it's still pending); the rest of the receipt is listed below
            var clickedLine = DialogLines.FirstOrDefault(l => l.Sale.Id == target.Id && l.IsEditable);
            if (clickedLine != null)
            {
                SetEditingLine(clickedLine);
                LoadItemIntoFields(clickedLine.Sale);
                DialogLines.Remove(clickedLine);
                OnPropertyChanged(nameof(HasDialogSales));
            }

            int count = receipt.EditableItems.Count + receipt.ReadOnlyItems.Count(s => !s.IsReturn);
            ReceiptNotice = clickedLine != null
                ? $"Editing \"{target.ProductName}\" ({count} item(s) on this receipt). Other items are listed below; use the add button to add another product."
                : $"\"{target.ProductName}\" is {DescribeLocked(target)} and can't be edited ({count} item(s) on this receipt). You can return it or add another product below.";

            IsSaleDialogVisible = true;
        }

        private static string DescribeLocked(SaleRecord sale) => sale.Status switch
        {
            PayoutStatus.Paid => "paid",
            PayoutStatus.ReturnedBeforePayout => "returned",
            _ => "locked"
        };

        /// <summary>Label of the add button: it saves the loaded item back to the list when one is being edited.</summary>
        [ObservableProperty] public partial string AddItemButtonText { get; private set; } = "+ Add Product to Receipt";

        private void SetEditingLine(ReceiptLineViewModel? line)
        {
            _editingLine = line;
            AddItemButtonText = line == null ? "+ Add Product to Receipt" : "✓ Done Editing, Add Another";
        }

        private void AddReceiptLines(ReceiptSnapshot receipt)
        {
            var receiptDay = receipt.ReceiptDate?.Date;

            // Existing editable items first, then anything new already in the list, then read-only items
            int insertAt = 0;
            foreach (var sale in receipt.EditableItems)
            {
                var line = new ReceiptLineViewModel(sale, isEditable: true, isSaved: true,
                    followsReceiptDate: sale.TransactionDate.Date == receiptDay);
                DialogLines.Insert(insertAt++, line);
            }

            foreach (var sale in receipt.ReadOnlyItems)
            {
                DialogLines.Add(new ReceiptLineViewModel(sale, isEditable: false, isSaved: true,
                    followsReceiptDate: sale.TransactionDate.Date == receiptDay));
            }

            OnPropertyChanged(nameof(HasDialogSales));
        }

        /// <summary>
        /// When adding a new receipt, typing an invoice number that already exists pulls that receipt in so the
        /// user can see what's already on it. Called when the invoice field loses focus, and before adding/saving.
        /// Changing the number again drops the loaded items and checks the new number.
        /// </summary>
        /// <returns>True if an existing receipt was just loaded into the dialog.</returns>
        public bool CheckInvoiceForExistingReceipt()
        {
            if (!IsSaleDialogVisible || IsEditing) return false;

            var key = InvoiceNumber.Trim();
            if (_loadedInvoice != null && string.Equals(_loadedInvoice, key, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (_loadedInvoice != null)
            {
                UnloadExistingReceipt();
            }

            if (key.Length == 0) return false;

            var receipt = _mainVm.Receipts.LoadReceipt(key);
            if (!receipt.Exists) return false;

            _loadedInvoice = receipt.InvoiceNumber;
            InvoiceNumber = receipt.InvoiceNumber; // use the stored spelling so the receipt isn't split
            _dateBeforeLoad = TransactionDate;
            _receiptDate = receipt.ReceiptDate;
            TransactionDate = receipt.ReceiptDate;

            AddReceiptLines(receipt);

            int count = receipt.EditableItems.Count + receipt.ReadOnlyItems.Count(s => !s.IsReturn);
            DialogTitle = $"Add to Existing Receipt {receipt.InvoiceNumber}";
            ReceiptNotice = $"{receipt.InvoiceNumber} already exists (dated {receipt.ReceiptDate:MMM d, yyyy}) with {count} item(s), shown below. " +
                            "Products you add will go on this receipt.";
            return true;
        }

        private void UnloadExistingReceipt()
        {
            DialogTitle = "Add New Transaction";
            foreach (var line in DialogLines.Where(l => l.IsSaved).ToList())
            {
                DialogLines.Remove(line);
                _stagedReturns.Remove(line.Sale.Id);
            }

            if (_editingLine is { IsSaved: true })
            {
                ClearProductFields();
            }

            _removedItemIds.Clear();
            TransactionDate = _dateBeforeLoad ?? DateTime.Now;
            _receiptDate = null;
            _loadedInvoice = null;
            _dateBeforeLoad = null;
            _newItemsUseToday = null;
            ReceiptNotice = string.Empty;
            OnPropertyChanged(nameof(HasDialogSales));
        }

        private void LoadItemIntoFields(SaleRecord item)
        {
            _suppressSuggestionFill = true;
            RecordType = item.RecordType;
            Sku = item.Sku;
            ProductName = item.ProductName;
            Category = item.Category;
            SalePrice = item.SalePrice;

            if (item is StandardSale std)
            {
                IsUsedGear = std.IsUsedGear;
            }
            else if (item is EbaySale ebay)
            {
                IsUsedGear = ebay.IsUsedGear;
            }
            else if (item is WarrantySale war)
            {
                WarrantyTypeName = war.WarrantyTypeName;
                ManufacturerPrice = war.ManufacturerPrice;
            }
            _suppressSuggestionFill = false;
        }

        [RelayCommand]
        private void CloseSaleDialog()
        {
            IsSaleDialogVisible = false;
            IsConfirmVisible = false;
        }

        [RelayCommand]
        private void AddDialogItem() => TryAddItem(after: null);

        /// <summary>
        /// Adds the product in the fields to the receipt, pausing for confirmation when needed:
        /// 1) a used SKU that has already been sold, 2) which date a product added to an existing receipt should use.
        /// <paramref name="after"/> runs once the item is added (e.g. to continue saving).
        /// </summary>
        private void TryAddItem(Action? after, bool skuConfirmed = false, bool? useToday = null)
        {
            if (string.IsNullOrWhiteSpace(ProductName))
            {
                after?.Invoke();
                return;
            }

            CheckInvoiceForExistingReceipt();

            if (!skuConfirmed && BuildSkuWarning() is { } warning)
            {
                ShowConfirm(
                    title: warning.Title,
                    message: warning.Message,
                    primaryText: warning.IsResaleOfReturnedItem ? "Yes, Selling It Again" : "Add Anyway",
                    primary: () => TryAddItem(after, skuConfirmed: true, useToday),
                    cancelText: "Go Back");
                return;
            }

            bool isNewItem = _editingLine == null;
            if (isNewItem && useToday == null && _receiptDate != null)
            {
                var receiptDay = (TransactionDate ?? DateTime.Today).Date;
                if (receiptDay != DateTime.Today)
                {
                    if (_newItemsUseToday is { } remembered)
                    {
                        useToday = remembered;
                    }
                    else
                    {
                        ShowConfirm(
                            title: "Which Date Should This Product Use?",
                            message: $"This receipt is dated {receiptDay:MMMM d, yyyy}. Products added to an existing receipt usually match its date, " +
                                     $"but you can date this one today ({DateTime.Today:MMMM d, yyyy}) instead. " +
                                     "Your choice applies to other products you add before saving.",
                            primaryText: $"Receipt Date ({receiptDay:MMM d})",
                            primary: () => { _newItemsUseToday = false; TryAddItem(after, skuConfirmed: true, useToday: false); },
                            secondaryText: $"Today ({DateTime.Today:MMM d})",
                            secondary: () => { _newItemsUseToday = true; TryAddItem(after, skuConfirmed: true, useToday: true); },
                            cancelText: "Go Back");
                        return;
                    }
                }
            }

            AddItemCore(useToday == true);
            after?.Invoke();
        }

        private void AddItemCore(bool useToday)
        {
            var previous = _editingLine?.Sale;

            SaleRecord item;
            if (previous != null && previous.RecordType == RecordType)
            {
                item = previous;
            }
            else
            {
                item = RecordType switch
                {
                    SaleType.Warranty => new WarrantySale(),
                    SaleType.Ebay => new EbaySale(),
                    _ => new StandardSale()
                };

                // Changing the type of an existing item keeps its identity (and so its database record)
                item.Id = previous?.Id ?? Guid.NewGuid();
                item.Status = PayoutStatus.Pending;
                item.TransactionDate = previous?.TransactionDate ?? DateTime.Now;
            }

            // Update item details from input fields
            item.InvoiceNumber = InvoiceNumber.Trim();
            item.Sku = Sku.Trim();
            item.ProductName = ProductName.Trim();
            item.Category = (RecordType == SaleType.Warranty) ? "Warranty" : Category;
            item.SalePrice = SalePrice;

            if (item is StandardSale std)
            {
                std.IsUsedGear = IsUsedGear;
            }
            else if (item is EbaySale ebay)
            {
                ebay.IsUsedGear = IsUsedGear;
            }
            else if (item is WarrantySale war)
            {
                war.WarrantyTypeName = WarrantyTypeName;
                war.ManufacturerPrice = ManufacturerPrice;
            }

            // Dates: an edited item keeps its date behaviour; a new one follows the receipt unless "today" was chosen
            // (Saved items keep their stored date here; whether they move with the receipt date is decided on save.)
            bool follows = _editingLine?.FollowsReceiptDate ?? !useToday;
            if (follows && _editingLine is not { IsSaved: true })
            {
                item.TransactionDate = OnDay(item.TransactionDate, TransactionDate ?? DateTime.Now);
            }
            else if (_editingLine == null)
            {
                item.TransactionDate = DateTime.Now;
            }

            var settings = _mainVm.LiteDb.GetUserSettings();
            _mainVm.CommissionProc.ApplyCommission(item, settings.ActiveRules ?? []);

            var line = new ReceiptLineViewModel(item, isEditable: true, isSaved: _editingLine?.IsSaved ?? false, followsReceiptDate: follows);
            if (_stagedReturns.TryGetValue(item.Id, out var staged))
            {
                line.StagedReturnText = DescribeStagedReturn(staged);
            }

            // Editable items are listed before the read-only ones
            int insertAt = DialogLines.TakeWhile(l => l.IsEditable).Count();
            DialogLines.Insert(insertAt, line);
            OnPropertyChanged(nameof(HasDialogSales));

            ClearProductFields();
        }

        /// <summary>The date part of <paramref name="day"/> with the time of day of <paramref name="original"/>.</summary>
        private static DateTime OnDay(DateTime original, DateTime day) => day.Date + original.TimeOfDay;

        // The SKU the user has already confirmed for the product in the fields (so they're asked once, not twice)
        private string? _confirmedSku;

        /// <summary>
        /// The duplicate-SKU notice for the used/eBay item in the fields, or null if there's nothing to say or it was
        /// already confirmed. An existing item whose SKU wasn't changed isn't re-checked.
        /// </summary>
        private SkuWarning? BuildSkuWarning()
        {
            if (!SkuCheckService.ShouldCheck(RecordType, IsUsedGear)) return null;

            var sku = Sku.Trim();
            if (sku.Length == 0) return null;
            if (string.Equals(_confirmedSku, sku, StringComparison.OrdinalIgnoreCase)) return null;

            if (_editingLine is { IsSaved: true } && string.Equals(_editingLine.Sale.Sku.Trim(), sku, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var selfId = _editingLine?.Sale.Id;
            var matches = _mainVm.SkuCheck
                .FindPriorSales(sku, selfId is { } id ? [id] : null)
                .Where(m => !_removedItemIds.Contains(m.Sale.Id))
                .ToList();

            var unsaved = DialogLines
                .Where(l => !l.IsSaved && l.Sale.Id != selfId && string.Equals(l.Sale.Sku.Trim(), sku, StringComparison.OrdinalIgnoreCase))
                .Select(l => $"Already on this receipt (not yet saved) · {l.Sale.ProductName} · {l.Sale.SalePrice:C}")
                .ToList();

            return SkuCheckService.BuildWarning(sku, matches, unsaved);
        }

        /// <summary>
        /// Called when the SKU field loses focus: warns straight away about a duplicate used/eBay SKU,
        /// rather than waiting until the product is added.
        /// </summary>
        public void CheckSkuOnLeave()
        {
            if (!IsSaleDialogVisible || IsConfirmVisible) return;
            if (BuildSkuWarning() is not { } warning) return;

            var sku = Sku.Trim();
            ShowConfirm(
                title: warning.Title,
                message: warning.Message,
                primaryText: warning.IsResaleOfReturnedItem ? "Yes, Selling It Again" : "Keep This SKU",
                primary: () => _confirmedSku = sku,
                cancelText: "Change SKU");
        }

        partial void OnSkuChanged(string value)
        {
            if (!string.Equals(_confirmedSku, value.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                _confirmedSku = null;
            }
        }

        [RelayCommand]
        private void RemoveDialogItem(ReceiptLineViewModel? line)
        {
            if (line == null || !line.IsEditable) return;

            DialogLines.Remove(line);
            if (line.IsSaved)
            {
                _removedItemIds.Add(line.Sale.Id);
            }
            _stagedReturns.Remove(line.Sale.Id);
            OnPropertyChanged(nameof(HasDialogSales));
        }

        [RelayCommand]
        private void EditDialogItem(ReceiptLineViewModel? line)
        {
            if (line == null || !line.IsEditable) return;

            // Protection: if the user started typing a new product, add it to the list first
            TryAddItem(after: () =>
            {
                if (!DialogLines.Contains(line)) return;
                SetEditingLine(line);
                LoadItemIntoFields(line.Sale);
                DialogLines.Remove(line);
                OnPropertyChanged(nameof(HasDialogSales));
            });
        }

        [RelayCommand]
        private void ReturnDialogItem(ReceiptLineViewModel? line)
        {
            if (line == null || !line.CanStageReturn) return;

            ReturnDialog.OpenForReturn(line.Sale, (amount, date) =>
            {
                var staged = new StagedReturn(line.Sale.Id, amount, date);
                _stagedReturns[line.Sale.Id] = staged;
                line.StagedReturnText = DescribeStagedReturn(staged);
                return null;
            }, isStaged: true);
        }

        [RelayCommand]
        private void UndoStagedReturn(ReceiptLineViewModel? line)
        {
            if (line == null) return;
            _stagedReturns.Remove(line.Sale.Id);
            line.StagedReturnText = string.Empty;
        }

        private static string DescribeStagedReturn(StagedReturn staged) =>
            $"Return of {staged.RefundAmount:C} will be applied on save";

        [RelayCommand]
        private void SaveSale()
        {
            DialogErrorMessage = string.Empty;

            if (string.IsNullOrWhiteSpace(InvoiceNumber))
            {
                DialogErrorMessage = "Enter an invoice number / order code.";
                return;
            }

            // If the invoice turns out to be an existing receipt, show it before saving anything onto it
            if (CheckInvoiceForExistingReceipt())
            {
                DialogErrorMessage = "This receipt already exists. Its items are now shown; review them, then click Save Entry again.";
                return;
            }

            // Auto-add current input details if they entered product details (may pause for a confirmation)
            TryAddItem(after: SaveReceiptCore);
        }

        /// <param name="moveExistingItems">
        /// Whether items already saved on the receipt follow a changed receipt date. Null = not decided yet: if the date
        /// was changed, the user is asked first (existing items never move silently).
        /// </param>
        private void SaveReceiptCore() => SaveReceiptCore(moveExistingItems: null);

        private void SaveReceiptCore(bool? moveExistingItems)
        {
            var editableLines = DialogLines.Where(l => l.IsEditable).ToList();
            if (editableLines.Count == 0 && _stagedReturns.Count == 0 && _removedItemIds.Count == 0)
            {
                DialogErrorMessage = "Add at least one product to the receipt.";
                return;
            }

            var receiptDay = TransactionDate ?? DateTime.Now;

            // Saved items whose date would change because the receipt date field was changed
            var savedItemsToMove = editableLines
                .Where(l => l.IsSaved && l.FollowsReceiptDate && l.Sale.TransactionDate.Date != receiptDay.Date)
                .ToList();

            if (savedItemsToMove.Count > 0 && moveExistingItems == null)
            {
                var oldDay = savedItemsToMove[0].Sale.TransactionDate;
                ShowConfirm(
                    title: "Change the Date of Existing Items?",
                    message: $"{savedItemsToMove.Count} item(s) already saved on {InvoiceNumber.Trim()} are dated {oldDay:MMMM d, yyyy}, " +
                             $"but the receipt date is now {receiptDay:MMMM d, yyyy}.\n\n" +
                             "Keep their original date, or move them to the new date? Products you're adding now use the new date either way.",
                    primaryText: $"Keep {oldDay:MMM d}",
                    primary: () => SaveReceiptCore(moveExistingItems: false),
                    secondaryText: $"Move to {receiptDay:MMM d}",
                    secondary: () => SaveReceiptCore(moveExistingItems: true),
                    cancelText: "Go Back");
                return;
            }

            // Items that follow the receipt date take the date field's day (keeping their time of day);
            // already-saved items only move if the user said so
            foreach (var line in editableLines.Where(l => l.FollowsReceiptDate && (!l.IsSaved || moveExistingItems == true)))
            {
                line.Sale.TransactionDate = OnDay(line.Sale.TransactionDate, receiptDay);
            }

            try
            {
                _mainVm.Receipts.SaveReceipt(
                    InvoiceNumber.Trim(),
                    editableLines.Select(l => l.Sale).ToList(),
                    _removedItemIds,
                    _stagedReturns.Values.ToList());
            }
            catch (Exception ex)
            {
                DialogErrorMessage = ex.Message;
                return;
            }

            IsSaleDialogVisible = false;
            LoadData();
        }

        // ------------------------------------------------------------------
        // Product name autofill
        // ------------------------------------------------------------------

        private void RefreshProductSuggestions()
        {
            _productSuggestions = _mainVm.Receipts.GetProductSuggestions();
            UpdateProductNameSuggestions();
        }

        /// <summary>Warranty plans and gear are suggested separately, matching the sale type being entered.</summary>
        private void UpdateProductNameSuggestions()
        {
            bool warranty = RecordType == SaleType.Warranty;
            ProductNameSuggestions = _productSuggestions
                .Where(s => s.IsWarranty == warranty)
                .Select(s => s.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        partial void OnRecordTypeChanged(SaleType value)
        {
            _ = value;
            UpdateProductNameSuggestions();
        }

        /// <summary>Picking a suggested name fills in the category (or warranty brand) it was last sold under.</summary>
        partial void OnSelectedProductSuggestionChanged(string? value)
        {
            if (_suppressSuggestionFill || string.IsNullOrEmpty(value)) return;

            bool warranty = RecordType == SaleType.Warranty;
            var suggestion = _productSuggestions.FirstOrDefault(s =>
                s.IsWarranty == warranty && string.Equals(s.Name, value, StringComparison.OrdinalIgnoreCase));
            if (suggestion == null) return;

            if (warranty)
            {
                if (WarrantyTypes.Contains(suggestion.WarrantyTypeName)) WarrantyTypeName = suggestion.WarrantyTypeName;
            }
            else if (Categories.Contains(suggestion.Category))
            {
                Category = suggestion.Category;
            }
        }

        // ------------------------------------------------------------------
        // Confirmation popup
        // ------------------------------------------------------------------

        private void ShowConfirm(string title, string message, string primaryText, Action primary,
            string cancelText = "Cancel", string secondaryText = "", Action? secondary = null)
        {
            ConfirmTitle = title;
            ConfirmMessage = message;
            ConfirmPrimaryText = primaryText;
            ConfirmSecondaryText = secondaryText;
            ConfirmCancelText = cancelText;
            _confirmPrimaryAction = primary;
            _confirmSecondaryAction = secondary;
            IsConfirmVisible = true;
        }

        [RelayCommand]
        private void ConfirmPrimary() => CompleteConfirm(_confirmPrimaryAction);

        [RelayCommand]
        private void ConfirmSecondary() => CompleteConfirm(_confirmSecondaryAction);

        [RelayCommand]
        private void ConfirmCancel() => CompleteConfirm(null);

        private void CompleteConfirm(Action? action)
        {
            IsConfirmVisible = false;
            _confirmPrimaryAction = null;
            _confirmSecondaryAction = null;
            action?.Invoke();
        }

        // ------------------------------------------------------------------
        // Grid actions: returns, deletes, restores
        // ------------------------------------------------------------------

        [RelayCommand]
        private void ProcessReturn(SaleRecord? parameter)
        {
            var target = parameter ?? SelectedSale;
            if (target == null || !target.CanProcessReturn) return;

            ReturnDialog.OpenForReturn(target, (amount, date) =>
            {
                try
                {
                    _mainVm.PayoutService.ProcessReturn(target, amount, date);
                }
                catch (InvalidOperationException ex)
                {
                    return ex.Message;
                }

                LoadData();
                return null;
            });
        }

        /// <summary>Changes the refund amount or date of a pending return line (through the return dialog).</summary>
        [RelayCommand]
        private void EditReturnLine(SaleRecord? parameter)
        {
            var returnLine = parameter ?? SelectedSale;
            if (returnLine == null || !returnLine.CanEditReturnLine) return;
            if (returnLine.OriginalSaleId is not { } originalId) return;

            var original = _mainVm.LiteDb.Sales.FindById(originalId);
            if (original == null)
            {
                LedgerMessage = "The original sale for this return no longer exists, so the return can't be edited.";
                return;
            }

            ReturnDialog.OpenForEditLine(returnLine, original, (amount, date) =>
            {
                try
                {
                    _mainVm.PayoutService.UpdateReturnLine(returnLine.Id, amount, date);
                }
                catch (InvalidOperationException ex)
                {
                    return ex.Message;
                }

                LoadData();
                return null;
            });
        }

        [RelayCommand]
        private void DeleteSale(SaleRecord? parameter)
        {
            var target = parameter ?? SelectedSale;
            if (target == null) return;

            try
            {
                // Also removes the sale's return lines, or re-balances siblings when deleting a return line
                _mainVm.PayoutService.DeleteSale(target.Id);
            }
            catch (InvalidOperationException ex)
            {
                LedgerMessage = ex.Message;
                return;
            }

            LoadData();
        }

        [RelayCommand]
        private void ShowRestoreReturnDialog(SaleRecord? parameter)
        {
            var target = parameter ?? SelectedSale;
            if (target == null || target.Status != PayoutStatus.ReturnedBeforePayout) return;

            SelectedSaleToRestore = target;
            IsRestoreReturnDialogVisible = true;
        }

        [RelayCommand]
        private void CloseRestoreReturnDialog()
        {
            IsRestoreReturnDialogVisible = false;
            SelectedSaleToRestore = null;
        }

        [RelayCommand]
        private void ExecuteRestoreReturn()
        {
            if (SelectedSaleToRestore == null) return;

            // Restore transaction status
            SelectedSaleToRestore.Status = PayoutStatus.Pending;

            // Recalculate commission based on current rules
            var settings = _mainVm.LiteDb.GetUserSettings();
            _mainVm.CommissionProc.ApplyCommission(SelectedSaleToRestore, settings.ActiveRules ?? []);

            // Save to operational ledger and sync
            _mainVm.LiteDb.Sales.Update(SelectedSaleToRestore);
            _mainVm.Sync.QueueUpsert(SelectedSaleToRestore);

            IsRestoreReturnDialogVisible = false;
            SelectedSaleToRestore = null;

            // Refresh views
            LoadData();
        }

        // CSV Import
        [RelayCommand]
        private void ShowImportDialog() => Import.Open();
    }

    public partial class CategoryBreakdownItem : ObservableObject
    {
        [ObservableProperty] public partial string Category { get; set; } = string.Empty;
        [ObservableProperty] public partial string ValueText { get; set; } = string.Empty;
        [ObservableProperty] public partial double Percentage { get; set; }
        [ObservableProperty] public partial string Color { get; set; } = "#3B82F6";
    }

    public class CategoryDisplay(string name, string valText)
    {
        public string Name { get; } = name;
        public string ValueText { get; } = valText;
    }

    public class ChartItem
    {
        private readonly TrendBucket _bucket;
        public double PositiveHeight { get; }
        public double NegativeHeight { get; }
        public bool HasNegativeValue => NegativeHeight > 0;
        public string Label => _bucket.Label;
        public List<CategoryDisplay> CategoriesBreakdown { get; } = new();

        public string TotalSalesText => _bucket.TotalRevenue.ToString("C2");
        public string TotalUnitsText => $"{_bucket.TotalQuantity} units";
        public string TotalCommissionText => _bucket.TotalCommission.ToString("C2");
        public string HighestSaleText => _bucket.MaxSalePrice.ToString("C2");

        public string StandardBreakdownText { get; }
        public string EbayBreakdownText { get; }
        public string WarrantyBreakdownText { get; }
        public string ReturnOffsetBreakdownText { get; }
        public bool HasReturnOffsets => _bucket.ReturnOffsetMetric.Quantity != 0 || _bucket.ReturnOffsetMetric.Revenue != 0;

        public ChartItem(TrendBucket bucket, double positiveHeight, double negativeHeight, string metricType)
        {
            _bucket = bucket ?? throw new ArgumentNullException(nameof(bucket));
            PositiveHeight = positiveHeight;
            NegativeHeight = negativeHeight;

            StandardBreakdownText = metricType switch
            {
                "Revenue" => _bucket.StandardMetric.Revenue.ToString("C2"),
                "Quantity" => $"{_bucket.StandardMetric.Quantity} units",
                "Commission" => _bucket.StandardMetric.Commission.ToString("C2"),
                _ => _bucket.StandardMetric.Revenue.ToString("C2")
            };

            EbayBreakdownText = metricType switch
            {
                "Revenue" => _bucket.EbayMetric.Revenue.ToString("C2"),
                "Quantity" => $"{_bucket.EbayMetric.Quantity} units",
                "Commission" => _bucket.EbayMetric.Commission.ToString("C2"),
                _ => _bucket.EbayMetric.Revenue.ToString("C2")
            };

            WarrantyBreakdownText = metricType switch
            {
                "Revenue" => _bucket.WarrantyMetric.Revenue.ToString("C2"),
                "Quantity" => $"{_bucket.WarrantyMetric.Quantity} units",
                "Commission" => _bucket.WarrantyMetric.Commission.ToString("C2"),
                _ => _bucket.WarrantyMetric.Revenue.ToString("C2")
            };

            ReturnOffsetBreakdownText = metricType switch
            {
                "Revenue" => _bucket.ReturnOffsetMetric.Revenue.ToString("C2"),
                "Quantity" => $"{_bucket.ReturnOffsetMetric.Quantity} units",
                "Commission" => _bucket.ReturnOffsetMetric.Commission.ToString("C2"),
                _ => _bucket.ReturnOffsetMetric.Revenue.ToString("C2")
            };

            foreach (var kvp in _bucket.CategoryBreakdown)
            {
                string catValText = metricType switch
                {
                    "Revenue" => kvp.Value.Revenue.ToString("C0"),
                    "Quantity" => $"{kvp.Value.Quantity} units",
                    "Commission" => kvp.Value.Commission.ToString("C0"),
                    _ => kvp.Value.Revenue.ToString("N0")
                };
                CategoriesBreakdown.Add(new CategoryDisplay(kvp.Key, catValText));
            }
        }
    }
}

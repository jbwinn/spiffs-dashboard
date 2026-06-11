using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using SalesLedger.Core.Models;
using SalesLedger.Core.Services;

namespace SalesLedger.Core.ViewModels
{
    public partial class LedgerDashboardViewModel : ObservableObject
    {
        private readonly MainWindowViewModel _mainVm;

        // KPI Properties
        [ObservableProperty] public partial decimal TotalSales { get; set; }
        [ObservableProperty] public partial int TotalUnitsSold { get; set; }
        [ObservableProperty] public partial decimal TotalCommission { get; set; }
        [ObservableProperty] public partial decimal AverageSalePrice { get; set; }

        // Timeframe & Metric selections
        [ObservableProperty] public partial string SelectedTimeframe { get; set; } = "Last 30 Days";
        [ObservableProperty] public partial string SelectedMetric { get; set; } = "Revenue"; // "Revenue", "Quantity", "Commission"
        [ObservableProperty] public partial bool HighValueSalesOnly { get; set; }

        // Collections
        public List<string> TimeframeOptions { get; } =
        [
            "Current Month", "Last Month", "Last 30 Days", "Last 3 Months", "Last 6 Months", "Year to Date"
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
            "All", "Standard (New)", "Standard (Used)", "eBay", "Warranty", "Return Offset"
        ];

        public List<string> DateFilterTypeOptions { get; } =
        [
            "All Time", "Current Month", "Last Month", "Last 30 Days", "Last 3 Months", "Last 6 Months", "Year to Date", "Custom"
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

        // CSV Import Wizard Properties
        [ObservableProperty] public partial bool IsImportDialogVisible { get; set; }
        [ObservableProperty] public partial string CsvFilePath { get; set; } = string.Empty;
        
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsStandardImport))]
        [NotifyPropertyChangedFor(nameof(IsWarrantyImport))]
        [NotifyPropertyChangedFor(nameof(IsEbayImport))]
        [NotifyPropertyChangedFor(nameof(IsStandardOrEbayImport))]
        public partial string ImportAsType { get; set; } = "Standard"; // "Standard", "Warranty", or "Ebay"

        public bool IsStandardImport => ImportAsType == "Standard";
        public bool IsWarrantyImport => ImportAsType == "Warranty";
        public bool IsEbayImport => ImportAsType == "Ebay";
        public bool IsStandardOrEbayImport => ImportAsType == "Standard" || ImportAsType == "Ebay";
        public ObservableCollection<string> CsvHeaders { get; } = [];

        [ObservableProperty] public partial string SelectedDateHeader { get; set; } = string.Empty;
        [ObservableProperty] public partial string SelectedInvoiceHeader { get; set; } = string.Empty;
        [ObservableProperty] public partial string SelectedSkuHeader { get; set; } = string.Empty;
        [ObservableProperty] public partial string SelectedProductNameHeader { get; set; } = string.Empty;
        [ObservableProperty] public partial string SelectedCategoryHeader { get; set; } = string.Empty;
        [ObservableProperty] public partial string SelectedPriceHeader { get; set; } = string.Empty;
        [ObservableProperty] public partial string SelectedIsUsedHeader { get; set; } = string.Empty;
        [ObservableProperty] public partial string SelectedWarrantyTypeHeader { get; set; } = string.Empty;
        [ObservableProperty] public partial string SelectedWholesalePriceHeader { get; set; } = string.Empty;

        [ObservableProperty] public partial string DefaultCategory { get; set; } = string.Empty;
        [ObservableProperty] public partial string DefaultWarrantyType { get; set; } = string.Empty;
        [ObservableProperty] public partial bool AllStandardAreUsed { get; set; } = true;

        // Selected Row context actions
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(EditSaleCommand))]
        [NotifyCanExecuteChangedFor(nameof(ProcessReturnCommand))]
        public partial SaleRecord? SelectedSale { get; set; }

        // Dialog state management
        [ObservableProperty] public partial bool IsSaleDialogVisible { get; set; }
        [ObservableProperty] public partial string DialogTitle { get; set; } = "Add New Transaction";
        [ObservableProperty] private partial bool IsEditing { get; set; }
        [ObservableProperty] public partial bool IsRestoreReturnDialogVisible { get; set; }
        public SaleRecord? SelectedSaleToRestore { get; set; }
        private Guid? _editingSaleId;

        // Dialog Bindings
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

        // Dropdown options
        public List<string> Categories => (_mainVm.LiteDb.GetUserSettings().ProductCategories ?? [])
            .Where(c => c.IsActive)
            .Select(c => c.Name)
            .ToList();

        public List<string> WarrantyTypes => (_mainVm.LiteDb.GetUserSettings().WarrantyTypes ?? [])
            .Where(w => w.IsActive)
            .Select(w => w.Name)
            .ToList();

        // Period close state
        [ObservableProperty] public partial bool IsPeriodCloseVisible { get; set; }
        [ObservableProperty] public partial string PeriodReportName { get; set; } = string.Empty;

        public LedgerDashboardViewModel(MainWindowViewModel mainVm)
        {
            _mainVm = mainVm ?? throw new ArgumentNullException(nameof(mainVm));
            RecentSalesView = new DataGridCollectionView(RecentSales);
            RecentSalesView.SortDescriptions.Add(DataGridSortDescription.FromPath("TransactionDate", System.ComponentModel.ListSortDirection.Descending));
            LoadData();
        }

        private void LoadData()
        {
            // Load KPIs
            var summary = _mainVm.Analytics.GetSummary(SelectedTimeframe);
            TotalSales = summary.TotalSales;
            TotalUnitsSold = summary.TotalUnitsSold;
            TotalCommission = summary.TotalCommission;
            AverageSalePrice = summary.AverageSalePrice;

            // Load Trends
            var trends = _mainVm.Analytics.GetTrends(SelectedTimeframe);
            ChartItems.Clear();

            double maxVal = 1.0;
            var items = new List<(TrendBucket Bucket, double PosVal, double NegVal)>();

            foreach (var bucket in trends)
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

            // Notify dependent dropdown properties
            OnPropertyChanged(nameof(Categories));
            OnPropertyChanged(nameof(WarrantyTypes));
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
            var trends = _mainVm.Analytics.GetTrends(SelectedTimeframe);
            var totals = new Dictionary<string, double>();
            double overallTotal = 0;

            foreach (var bucket in trends)
            {
                foreach (var kvp in bucket.CategoryBreakdown)
                {
                    double val = CategoryBreakdownMetric == "Dollar Amount" ? kvp.Value.PositiveRevenue : kvp.Value.PositiveQuantity;
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

            if (SelectedDateFilterType == "Custom" && CustomStartDate.HasValue && CustomEndDate.HasValue)
            {
                start = CustomStartDate.Value.Date;
                end = CustomEndDate.Value.Date.AddDays(1).AddMilliseconds(-1);
            }
            else if (SelectedDateFilterType != "All Time")
            {
                var range = _mainVm.Analytics.GetDateRangeAndScale(SelectedDateFilterType);
                start = range.Start;
                end = range.End;
            }

            // Fetch from LiteDB for display accuracy and direct edit hookups
            var query = _mainVm.LiteDb.Sales.Find(x => x.TransactionDate >= start && x.TransactionDate <= end);
            
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
                    "Standard (New)" => query.Where(x => x.RecordType == SaleType.Standard && x is StandardSale { IsUsedGear: false }),
                    "Standard (Used)" => query.Where(x => x.RecordType == SaleType.Standard && x is StandardSale { IsUsedGear: true }),
                    "eBay" => query.Where(x => x.RecordType == SaleType.Ebay),
                    "Warranty" => query.Where(x => x.RecordType == SaleType.Warranty),
                    "Return Offset" => query.Where(x => x.RecordType == SaleType.ReturnOffset),
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
            Avalonia.Threading.Dispatcher.UIThread.Post(LoadData);
        }

        // Open Dialog
        [RelayCommand]
        private void ShowAddSaleDialog()
        {
            DialogTitle = "Add New Transaction";
            IsEditing = false;
            _editingSaleId = null;

            InvoiceNumber = string.Empty;
            Sku = string.Empty;
            ProductName = string.Empty;
            Category = Categories.FirstOrDefault() ?? string.Empty;
            SalePrice = 0m;
            TransactionDate = DateTime.Now;
            RecordType = SaleType.Standard;
            IsUsedGear = true;
            WarrantyTypeName = WarrantyTypes.FirstOrDefault() ?? string.Empty;
            ManufacturerPrice = 0m;

            IsSaleDialogVisible = true;
        }

        [RelayCommand]
        private void EditSale(SaleRecord? parameter)
        {
            var target = parameter ?? SelectedSale;
            if (target == null || target.Status == PayoutStatus.ReturnedBeforePayout) return;

            DialogTitle = "Edit Transaction";
            IsEditing = true;
            _editingSaleId = target.Id;

            InvoiceNumber = target.InvoiceNumber;
            Sku = target.Sku;
            ProductName = target.ProductName;
            Category = target.Category;
            SalePrice = target.SalePrice;
            TransactionDate = target.TransactionDate;
            RecordType = target.RecordType;

            if (target is StandardSale std)
            {
                IsUsedGear = std.IsUsedGear;
            }
            else if (target is EbaySale ebay)
            {
                IsUsedGear = ebay.IsUsedGear;
            }
            else if (target is WarrantySale war)
            {
                WarrantyTypeName = war.WarrantyTypeName;
                ManufacturerPrice = war.ManufacturerPrice;
            }

            IsSaleDialogVisible = true;
        }

        [RelayCommand]
        private void CloseSaleDialog()
        {
            IsSaleDialogVisible = false;
        }

        [RelayCommand]
        private void SaveSale()
        {
            if (string.IsNullOrWhiteSpace(InvoiceNumber) || string.IsNullOrWhiteSpace(ProductName))
            {
                return;
            }

            SaleRecord sale;
            bool typeChanged = false;
            SaleRecord? existing = null;
            if (IsEditing && _editingSaleId.HasValue)
            {
                existing = _mainVm.LiteDb.Sales.FindById(_editingSaleId.Value);
                if (existing == null || existing.Status == PayoutStatus.ReturnedBeforePayout)
                {
                    IsSaleDialogVisible = false;
                    return;
                }
                
                if (existing.RecordType != RecordType)
                {
                    typeChanged = true;
                }
            }

            if (typeChanged || !IsEditing)
            {
                if (RecordType == SaleType.Standard)
                {
                    sale = new StandardSale();
                }
                else if (RecordType == SaleType.Warranty)
                {
                    sale = new WarrantySale();
                }
                else
                {
                    sale = new EbaySale();
                }
                
                if (IsEditing && existing != null)
                {
                    sale.Id = existing.Id;
                    sale.Status = existing.Status;
                    sale.AssociatedReportId = existing.AssociatedReportId;
                }
                else
                {
                    sale.Status = PayoutStatus.Pending;
                }
            }
            else
            {
                sale = existing!;
            }

            sale.InvoiceNumber = InvoiceNumber.Trim();
            sale.Sku = Sku.Trim();
            sale.ProductName = ProductName.Trim();
            sale.Category = (RecordType == SaleType.Warranty) ? "Warranty" : Category;
            sale.SalePrice = SalePrice;
            sale.TransactionDate = TransactionDate ?? DateTime.Now;

            if (sale is StandardSale std)
            {
                std.IsUsedGear = IsUsedGear;
            }
            else if (sale is EbaySale ebay)
            {
                ebay.IsUsedGear = IsUsedGear;
            }
            else if (sale is WarrantySale war)
            {
                war.WarrantyTypeName = WarrantyTypeName;
                war.ManufacturerPrice = ManufacturerPrice;
            }

            // Calculate commission payout
            var settings = _mainVm.LiteDb.GetUserSettings();
            sale.CalculatedCommission = _mainVm.CommissionProc.CalculateLineItem(sale, settings.ActiveRules ?? []);

            // Save to operational document ledger
            if (IsEditing)
            {
                _mainVm.LiteDb.Sales.Update(sale);
            }
            else
            {
                _mainVm.LiteDb.Sales.Insert(sale);
            }

            // Mirror synchronously/asynchronously to DuckDB OLAP
            _mainVm.Sync.QueueUpsert(sale);

            IsSaleDialogVisible = false;
            
            // Refresh views
            LoadData();
        }

        [RelayCommand]
        private void ProcessReturn(SaleRecord? parameter)
        {
            var target = parameter ?? SelectedSale;
            if (target == null || target.Status == PayoutStatus.ReturnedBeforePayout) return;

            // Execute transactional returns pipeline (pre-payout vs post-payout logic)
            _mainVm.PayoutService.ProcessReturn(target);

            // Refresh views
            LoadData();
        }

        [RelayCommand]
        private void DeleteSale(SaleRecord? parameter)
        {
            var target = parameter ?? SelectedSale;
            if (target == null) return;

            // Delete from LiteDB
            _mainVm.LiteDb.Sales.Delete(target.Id);

            // Queue deletion in SyncPipeline for DuckDB
            _mainVm.Sync.QueueDelete(target.Id);

            // Refresh views
            LoadData();
        }

        // Close Period dialogs
        [RelayCommand]
        private void ShowPeriodCloseDialog()
        {
            PeriodReportName = $"{DateTime.Now:MMMM yyyy} Submission";
            IsPeriodCloseVisible = true;
        }

        [RelayCommand]
        private void ClosePeriodCloseDialog()
        {
            IsPeriodCloseVisible = false;
        }

        [RelayCommand]
        private void ExecutePeriodClose()
        {
            if (string.IsNullOrWhiteSpace(PeriodReportName)) return;

            // Freeze cycle
            var report = _mainVm.PayoutService.CloseCurrentPayPeriod(PeriodReportName.Trim());

            // Build print-ready PDF statement
            var settings = _mainVm.LiteDb.GetUserSettings();
            var salesInReport = _mainVm.LiteDb.Sales.Find(x => x.AssociatedReportId == report.Id).ToList();

            var downloadsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads",
                $"CommissionReport_{report.ReportName.Replace(" ", "_")}.pdf"
            );

            _mainVm.ReportGen.GeneratePdfReport(report, salesInReport, settings, downloadsPath);

            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = downloadsPath,
                    UseShellExecute = true
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception)
            {
                // Ignore launch failures (e.g. headless environment or no pdf viewer)
            }

            IsPeriodCloseVisible = false;
            
            // Refresh UI
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
            SelectedSaleToRestore.CalculatedCommission = _mainVm.CommissionProc.CalculateLineItem(SelectedSaleToRestore, settings.ActiveRules ?? []);

            // Save to operational ledger and sync
            _mainVm.LiteDb.Sales.Update(SelectedSaleToRestore);
            _mainVm.Sync.QueueUpsert(SelectedSaleToRestore);

            IsRestoreReturnDialogVisible = false;
            SelectedSaleToRestore = null;

            // Refresh views
            LoadData();
        }

        [RelayCommand]
        private void PreviewPeriodReport()
        {
            // Gather all active pending and returned-before-payout sales (AssociatedReportId is null)
            var pendingSales = _mainVm.LiteDb.Sales.Find(x => x.Status == PayoutStatus.Pending).ToList();
            var returnedBeforePayoutSales = _mainVm.LiteDb.Sales.Find(x => x.Status == PayoutStatus.ReturnedBeforePayout && x.AssociatedReportId == null).ToList();

            var allPeriodSales = pendingSales.Concat(returnedBeforePayoutSales).ToList();

            // Create temporary payout report metadata
            var tempReport = new PayoutReport
            {
                Id = Guid.Empty,
                ReportGeneratedTimestamp = DateTime.UtcNow,
                ReportName = "Preview (Open Period)",
                TotalCommissionCalculated = pendingSales.Sum(s => s.CalculatedCommission),
                LockedSaleIds = allPeriodSales.Select(s => s.Id).ToList()
            };

            var settings = _mainVm.LiteDb.GetUserSettings();
            var downloadsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads",
                "CommissionReport_Preview.pdf"
            );

            _mainVm.ReportGen.GeneratePdfReport(tempReport, allPeriodSales, settings, downloadsPath);

            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = downloadsPath,
                    UseShellExecute = true
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception)
            {
                // Ignore launch failures
            }
        }

        // CSV Import Wizard Commands
        [RelayCommand]
        private void ShowImportDialog()
        {
            CsvFilePath = string.Empty;
            CsvHeaders.Clear();
            ImportAsType = "Standard";
            
            SelectedDateHeader = string.Empty;
            SelectedInvoiceHeader = string.Empty;
            SelectedSkuHeader = string.Empty;
            SelectedProductNameHeader = string.Empty;
            SelectedCategoryHeader = string.Empty;
            SelectedPriceHeader = string.Empty;
            SelectedIsUsedHeader = string.Empty;
            SelectedWarrantyTypeHeader = string.Empty;
            SelectedWholesalePriceHeader = string.Empty;
            
            DefaultCategory = Categories.FirstOrDefault() ?? "Lens";
            DefaultWarrantyType = WarrantyTypes.FirstOrDefault() ?? "Sony";
            AllStandardAreUsed = true;

            IsImportDialogVisible = true;
        }

        [RelayCommand]
        private void CloseImportDialog()
        {
            IsImportDialogVisible = false;
        }

        [RelayCommand]
        private async Task BrowseCsvFile()
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: not null } desktop)
            {
                var csvType = new FilePickerFileType("CSV Files")
                {
                    Patterns = [ "*.csv" ],
                    MimeTypes = [ "text/csv", "text/plain" ]
                };

                var files = await desktop.MainWindow.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Select Spiff Sheet CSV File",
                    AllowMultiple = false,
                    FileTypeFilter = [ csvType ]
                });

                if (files is { Count: > 0 })
                {
                    CsvFilePath = files[0].Path.LocalPath;
                    ParseCsvHeaders();
                }
            }
        }

        private void ParseCsvHeaders()
        {
            if (string.IsNullOrEmpty(CsvFilePath)) return;

            try
            {
                var rows = CsvHelper.ParseCsv(CsvFilePath);
                if (rows.Count > 0)
                {
                    var headers = rows[0];
                    CsvHeaders.Clear();
                    CsvHeaders.Add("[None]");
                    foreach (var h in headers)
                    {
                        CsvHeaders.Add(h);
                    }

                    // Auto-map headers
                    SelectedDateHeader = AutoMatchHeader(headers, "date", "trans");
                    SelectedInvoiceHeader = AutoMatchHeader(headers, "receipt", "invoice", "number");
                    SelectedSkuHeader = AutoMatchHeader(headers, "sku", "product sku", "item");
                    SelectedProductNameHeader = AutoMatchHeader(headers, "product name", "name", "description");
                    SelectedCategoryHeader = AutoMatchHeader(headers, "category", "product type", "type");
                    SelectedPriceHeader = AutoMatchHeader(headers, "price", "sale price", "product price");
                    SelectedIsUsedHeader = AutoMatchHeader(headers, "used", "is used", "gear");
                    SelectedWarrantyTypeHeader = AutoMatchHeader(headers, "warranty type", "plan");
                    SelectedWholesalePriceHeader = AutoMatchHeader(headers, "wholesale", "canon unit", "cost");
                }
            }
            catch (Exception)
            {
                // fail silently
            }
        }

        private string AutoMatchHeader(string[] headers, params string[] keywords)
        {
            foreach (var h in headers)
            {
                string hl = h.ToLowerInvariant();
                foreach (var kw in keywords)
                {
                    if (hl.Contains(kw)) return h;
                }
            }
            return "[None]";
        }

        [RelayCommand]
        private void ExecuteCsvImport()
        {
            if (string.IsNullOrEmpty(CsvFilePath)) return;

            try
            {
                var rows = CsvHelper.ParseCsv(CsvFilePath);
                if (rows.Count <= 1) return;

                var headers = rows[0].ToList();
                int dateIdx = headers.IndexOf(SelectedDateHeader);
                int invIdx = headers.IndexOf(SelectedInvoiceHeader);
                int skuIdx = headers.IndexOf(SelectedSkuHeader);
                int nameIdx = headers.IndexOf(SelectedProductNameHeader);
                int catIdx = headers.IndexOf(SelectedCategoryHeader);
                int priceIdx = headers.IndexOf(SelectedPriceHeader);

                int usedIdx = headers.IndexOf(SelectedIsUsedHeader);
                int warTypeIdx = headers.IndexOf(SelectedWarrantyTypeHeader);
                int wholesaleIdx = headers.IndexOf(SelectedWholesalePriceHeader);

                var settings = _mainVm.LiteDb.GetUserSettings();

                for (int i = 1; i < rows.Count; i++)
                {
                    var row = rows[i];
                    
                    string GetVal(int idx) => (idx >= 0 && idx < row.Length) ? row[idx] : string.Empty;

                    string dateStr = GetVal(dateIdx);
                    string invStr = GetVal(invIdx);
                    string skuStr = GetVal(skuIdx);
                    string nameStr = GetVal(nameIdx);
                    string catStr = GetVal(catIdx);
                    string priceStr = GetVal(priceIdx);

                    if (string.IsNullOrWhiteSpace(skuStr) && string.IsNullOrWhiteSpace(nameStr))
                        continue;
                    if (dateStr.ToLowerInvariant().Contains("total") || skuStr.ToLowerInvariant().Contains("total") || nameStr.ToLowerInvariant().Contains("total"))
                        continue;

                    if (!DateTime.TryParse(dateStr, out DateTime txDate))
                    {
                        txDate = DateTime.Now;
                    }

                    priceStr = priceStr.Replace("$", "").Replace(",", "").Trim();
                    if (!decimal.TryParse(priceStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out decimal price))
                    {
                        price = 0m;
                    }

                    string category = "Warranty";
                    if (ImportAsType == "Standard" || ImportAsType == "Ebay")
                    {
                        category = !string.IsNullOrEmpty(catStr) ? catStr : DefaultCategory;
                        var matchingCategory = (settings.ProductCategories ?? []).FirstOrDefault(c => c.Name.Equals(category, StringComparison.OrdinalIgnoreCase));
                        category = matchingCategory == null ? DefaultCategory : matchingCategory.Name;
                    }

                    SaleRecord record;
                    if (ImportAsType == "Standard")
                    {
                        var std = new StandardSale();
                        bool isUsed = AllStandardAreUsed;
                        if (usedIdx >= 0 && usedIdx < row.Length)
                        {
                            string uVal = row[usedIdx].ToLowerInvariant();
                            if (uVal.Contains("used") || uVal == "yes" || uVal == "true" || uVal == "1")
                            {
                                isUsed = true;
                            }
                        }
                        std.IsUsedGear = isUsed;
                        record = std;
                    }
                    else if (ImportAsType == "Ebay")
                    {
                        var ebay = new EbaySale();
                        bool isUsed = AllStandardAreUsed;
                        if (usedIdx >= 0 && usedIdx < row.Length)
                        {
                            string uVal = row[usedIdx].ToLowerInvariant();
                            if (uVal.Contains("used") || uVal == "yes" || uVal == "true" || uVal == "1")
                            {
                                isUsed = true;
                            }
                        }
                        ebay.IsUsedGear = isUsed;
                        record = ebay;
                    }
                    else
                    {
                        var war = new WarrantySale();
                        string warType = GetVal(warTypeIdx);
                        if (string.IsNullOrEmpty(warType))
                        {
                            warType = DefaultWarrantyType;
                        }
                        
                        string wholesaleStr = GetVal(wholesaleIdx).Replace("$", "").Replace(",", "").Trim();
                        if (!decimal.TryParse(wholesaleStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out decimal wholesale))
                        {
                            wholesale = 0m;
                        }

                        var matchingWar = (settings.WarrantyTypes ?? []).FirstOrDefault(w => w.Name.Equals(warType, StringComparison.OrdinalIgnoreCase));
                        war.WarrantyTypeName = matchingWar == null ? DefaultWarrantyType : matchingWar.Name;
                        
                        war.ManufacturerPrice = wholesale;
                        record = war;
                    }

                    record.InvoiceNumber = !string.IsNullOrEmpty(invStr) ? invStr : $"CSV-{DateTime.Now.Ticks}";
                    record.Sku = skuStr;
                    record.ProductName = nameStr;
                    record.Category = category;
                    record.SalePrice = price;
                    record.TransactionDate = txDate;
                    record.Status = PayoutStatus.Pending;

                    record.CalculatedCommission = _mainVm.CommissionProc.CalculateLineItem(record, settings.ActiveRules ?? []);

                    _mainVm.LiteDb.Sales.Insert(record);
                    _mainVm.Sync.QueueUpsert(record);
                }

                IsImportDialogVisible = false;
                LoadData();
            }
            catch (Exception)
            {
                // fail silently
            }
        }
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

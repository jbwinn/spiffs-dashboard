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
        [ObservableProperty] private decimal _totalSales;
        [ObservableProperty] private int _totalUnitsSold;
        [ObservableProperty] private decimal _totalCommission;
        [ObservableProperty] private decimal _averageSalePrice;

        // Timeframe & Metric selections
        [ObservableProperty] private string _selectedTimeframe = "Last 30 Days";
        [ObservableProperty] private string _selectedMetric = "Revenue"; // "Revenue", "Quantity", "Commission"
        [ObservableProperty] private bool _highValueSalesOnly;

        // Collections
        public List<string> TimeframeOptions { get; } = new()
        {
            "Current Month", "Last Month", "Last 30 Days", "Last 3 Months", "Last 6 Months", "Year to Date"
        };

        public List<string> MetricOptions { get; } = new() { "Revenue", "Quantity", "Commission" };

        public ObservableCollection<SaleRecord> RecentSales { get; } = new();
        public DataGridCollectionView RecentSalesView { get; }
        public ObservableCollection<ChartItem> ChartItems { get; } = new();

        // Ledger Search & Filter Panel Properties
        [ObservableProperty] private string _searchText = string.Empty;
        [ObservableProperty] private string _selectedSaleTypeFilter = "All";
        [ObservableProperty] private string _selectedCategoryFilter = "All";
        [ObservableProperty] private string _selectedDateFilterType = "All Time";
        [ObservableProperty] private DateTime? _customStartDate = DateTime.Now.AddDays(-30);
        [ObservableProperty] private DateTime? _customEndDate = DateTime.Now;

        public bool IsCustomDateFilter => SelectedDateFilterType == "Custom";

        public List<string> SaleTypeFilterOptions { get; } = new()
        {
            "All", "Standard (New)", "Standard (Used)", "eBay", "Warranty", "Return Offset"
        };

        public List<string> DateFilterTypeOptions { get; } = new()
        {
            "All Time", "Current Month", "Last Month", "Last 30 Days", "Last 3 Months", "Last 6 Months", "Year to Date", "Custom"
        };

        public ObservableCollection<string> CategoryFilterOptions { get; } = new() { "All" };

        // Category Breakdown Aggregations
        [ObservableProperty] private string _categoryBreakdownMetric = "Dollar Amount"; // "Dollar Amount" or "Quantity"
        public ObservableCollection<CategoryBreakdownItem> CategoryBreakdownItems { get; } = new();
        
        public List<string> CategoryBreakdownMetricOptions { get; } = new() { "Dollar Amount", "Quantity" };

        private static readonly string[] CategoryColors = new[]
        {
            "#3B82F6", // Blue
            "#10B981", // Emerald
            "#F59E0B", // Amber
            "#EC4899", // Pink
            "#8B5CF6", // Purple
            "#6366F1", // Indigo
            "#14B8A6", // Teal
            "#F97316", // Orange
            "#06B6D4", // Cyan
            "#A855F7"  // Purple-Light
        };

        // CSV Import Wizard Properties
        [ObservableProperty] private bool _isImportDialogVisible;
        [ObservableProperty] private string _csvFilePath = string.Empty;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsStandardImport))]
        [NotifyPropertyChangedFor(nameof(IsWarrantyImport))]
        [NotifyPropertyChangedFor(nameof(IsEbayImport))]
        [NotifyPropertyChangedFor(nameof(IsStandardOrEbayImport))]
        private string _importAsType = "Standard"; // "Standard", "Warranty", or "Ebay"

        public bool IsStandardImport => ImportAsType == "Standard";
        public bool IsWarrantyImport => ImportAsType == "Warranty";
        public bool IsEbayImport => ImportAsType == "Ebay";
        public bool IsStandardOrEbayImport => ImportAsType == "Standard" || ImportAsType == "Ebay";
        public ObservableCollection<string> CsvHeaders { get; } = new();

        [ObservableProperty] private string _selectedDateHeader = string.Empty;
        [ObservableProperty] private string _selectedInvoiceHeader = string.Empty;
        [ObservableProperty] private string _selectedSkuHeader = string.Empty;
        [ObservableProperty] private string _selectedProductNameHeader = string.Empty;
        [ObservableProperty] private string _selectedCategoryHeader = string.Empty;
        [ObservableProperty] private string _selectedPriceHeader = string.Empty;
        [ObservableProperty] private string _selectedIsUsedHeader = string.Empty;
        [ObservableProperty] private string _selectedWarrantyTypeHeader = string.Empty;
        [ObservableProperty] private string _selectedWholesalePriceHeader = string.Empty;

        [ObservableProperty] private string _defaultCategory = string.Empty;
        [ObservableProperty] private string _defaultWarrantyType = string.Empty;
        [ObservableProperty] private bool _allStandardAreUsed = true;

        // Selected Row context actions
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(EditSaleCommand))]
        [NotifyCanExecuteChangedFor(nameof(ProcessReturnCommand))]
        private SaleRecord? _selectedSale;

        // Dialog state management
        [ObservableProperty] private bool _isSaleDialogVisible;
        [ObservableProperty] private string _dialogTitle = "Add New Transaction";
        [ObservableProperty] private bool _isEditing;
        private Guid? _editingSaleId;

        // Dialog Bindings
        [ObservableProperty] private string _invoiceNumber = string.Empty;
        [ObservableProperty] private string _sku = string.Empty;
        [ObservableProperty] private string _productName = string.Empty;
        [ObservableProperty] private string _category = string.Empty;
        [ObservableProperty] private decimal _salePrice;
        [ObservableProperty] private DateTime? _transactionDate = DateTime.Now;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SelectedRecordTypeIndex))]
        [NotifyPropertyChangedFor(nameof(InvoiceLabel))]
        [NotifyPropertyChangedFor(nameof(InvoicePlaceholder))]
        [NotifyPropertyChangedFor(nameof(ShowCategory))]
        [NotifyPropertyChangedFor(nameof(ShowUsedGear))]
        [NotifyPropertyChangedFor(nameof(ShowWarranty))]
        private SaleType _recordType = SaleType.Standard;

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
        [ObservableProperty] private bool _isUsedGear;
        
        // Warranty sale specific
        [ObservableProperty] private string _warrantyTypeName = string.Empty;
        [ObservableProperty] private decimal _manufacturerPrice;

        // Dropdown options
        public List<string> Categories => _mainVm.LiteDb.GetUserSettings().ProductCategories
            .Where(c => c.IsActive)
            .Select(c => c.Name)
            .ToList();

        public List<string> WarrantyTypes => _mainVm.LiteDb.GetUserSettings().WarrantyTypes
            .Where(w => w.IsActive)
            .Select(w => w.Name)
            .ToList();

        // Period close state
        [ObservableProperty] private bool _isPeriodCloseVisible;
        [ObservableProperty] private string _periodReportName = string.Empty;

        // Tooltip metadata
        [ObservableProperty] private TrendBucket? _hoveredBucket;
        [ObservableProperty] private bool _isTooltipVisible;

        public LedgerDashboardViewModel(MainWindowViewModel mainVm)
        {
            _mainVm = mainVm ?? throw new ArgumentNullException(nameof(mainVm));
            RecentSalesView = new DataGridCollectionView(RecentSales);
            RecentSalesView.SortDescriptions.Add(DataGridSortDescription.FromPath("TransactionDate", System.ComponentModel.ListSortDirection.Descending));
            LoadData();
        }

        public void LoadData()
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

            double maxValue = 1.0;
            var items = new List<(TrendBucket Bucket, double Value)>();
            foreach (var bucket in trends)
            {
                double val = SelectedMetric switch
                {
                    "Revenue" => bucket.TotalRevenue,
                    "Quantity" => bucket.TotalQuantity,
                    "Commission" => bucket.TotalCommission,
                    _ => bucket.TotalRevenue
                };
                items.Add((bucket, val));
                if (val > maxValue) maxValue = val;
            }

            foreach (var item in items)
            {
                double pct = item.Value / maxValue;
                double h = pct * 180; // max chart height = 180px
                if (h < 4 && item.Value > 0) h = 4; // minimum visible height
                
                var chartItem = new ChartItem(item.Bucket, item.Value, h, SelectedMetric);
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
            if (CategoryFilterOptions.Contains(selected))
            {
                SelectedCategoryFilter = selected;
            }
            else
            {
                SelectedCategoryFilter = "All";
            }
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
                    double val = CategoryBreakdownMetric == "Dollar Amount" ? kvp.Value.Revenue : kvp.Value.Quantity;
                    if (!totals.ContainsKey(kvp.Key))
                    {
                        totals[kvp.Key] = 0;
                    }
                    totals[kvp.Key] += val;
                    overallTotal += val;
                }
            }

            CategoryBreakdownItems.Clear();
            int colorIndex = 0;
            foreach (var kvp in totals.OrderByDescending(x => x.Value))
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
                    Value = kvp.Value,
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
                    "Standard (New)" => query.Where(x => x.RecordType == SaleType.Standard && (x is StandardSale s && !s.IsUsedGear)),
                    "Standard (Used)" => query.Where(x => x.RecordType == SaleType.Standard && (x is StandardSale s && s.IsUsedGear)),
                    "eBay" => query.Where(x => x.RecordType == SaleType.Ebay),
                    "Warranty" => query.Where(x => x.RecordType == SaleType.Warranty),
                    "Return Offset" => query.Where(x => x.RecordType == SaleType.ReturnOffset),
                    _ => query
                };
            }

            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                string search = SearchText.Trim().ToLowerInvariant();
                query = query.Where(x => 
                    (x.InvoiceNumber != null && x.InvoiceNumber.ToLowerInvariant().Contains(search)) ||
                    (x.ProductName != null && x.ProductName.ToLowerInvariant().Contains(search)) ||
                    (x.Sku != null && x.Sku.ToLowerInvariant().Contains(search))
                );
            }

            RecentSales.Clear();
            foreach (var sale in query.OrderByDescending(s => s.TransactionDate))
            {
                RecentSales.Add(sale);
            }
        }

        partial void OnSelectedTimeframeChanged(string value) => LoadData();
        partial void OnSelectedMetricChanged(string value) => LoadData();
        partial void OnCategoryBreakdownMetricChanged(string value) => UpdateCategoryBreakdown();
        partial void OnHighValueSalesOnlyChanged(bool value) => LoadSalesList();
        partial void OnSearchTextChanged(string value) => LoadSalesList();
        partial void OnSelectedSaleTypeFilterChanged(string value) => LoadSalesList();
        partial void OnSelectedCategoryFilterChanged(string value) => LoadSalesList();
        partial void OnSelectedDateFilterTypeChanged(string value)
        {
            OnPropertyChanged(nameof(IsCustomDateFilter));
            LoadSalesList();
        }
        partial void OnCustomStartDateChanged(DateTime? value) => LoadSalesList();
        partial void OnCustomEndDateChanged(DateTime? value) => LoadSalesList();

        // Open Dialog
        [RelayCommand]
        public void ShowAddSaleDialog()
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
        public void EditSale(SaleRecord? parameter)
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

        private bool CanEditOrReturn()
        {
            return SelectedSale != null && SelectedSale.Status != PayoutStatus.ReturnedBeforePayout;
        }

        [RelayCommand]
        public void CloseSaleDialog()
        {
            IsSaleDialogVisible = false;
        }

        [RelayCommand]
        public void SaveSale()
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
            sale.CalculatedCommission = _mainVm.CommissionProc.CalculateLineItem(sale, settings.ActiveRules);

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
        public void ProcessReturn(SaleRecord? parameter)
        {
            var target = parameter ?? SelectedSale;
            if (target == null || target.Status == PayoutStatus.ReturnedBeforePayout) return;

            // Execute transactional returns pipeline (pre-payout vs post-payout logic)
            _mainVm.PayoutService.ProcessReturn(target);

            // Refresh views
            LoadData();
        }

        [RelayCommand]
        public void DeleteSale(SaleRecord? parameter)
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
        public void ShowPeriodCloseDialog()
        {
            PeriodReportName = $"{DateTime.Now:MMMM yyyy} Submission";
            IsPeriodCloseVisible = true;
        }

        [RelayCommand]
        public void ClosePeriodCloseDialog()
        {
            IsPeriodCloseVisible = false;
        }

        [RelayCommand]
        public void ExecutePeriodClose()
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

        // CSV Import Wizard Commands
        [RelayCommand]
        public void ShowImportDialog()
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
        public void CloseImportDialog()
        {
            IsImportDialogVisible = false;
        }

        [RelayCommand]
        public async Task BrowseCsvFile()
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
            {
                var csvType = new FilePickerFileType("CSV Files")
                {
                    Patterns = new[] { "*.csv" },
                    MimeTypes = new[] { "text/csv", "text/plain" }
                };

                var files = await desktop.MainWindow.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Select Spiff Sheet CSV File",
                    AllowMultiple = false,
                    FileTypeFilter = new[] { csvType }
                });

                if (files != null && files.Count > 0)
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
        public void ExecuteCsvImport()
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
                        var matchingCategory = settings.ProductCategories.FirstOrDefault(c => c.Name.Equals(category, StringComparison.OrdinalIgnoreCase));
                        if (matchingCategory == null)
                        {
                            category = DefaultCategory;
                        }
                        else
                        {
                            category = matchingCategory.Name;
                        }
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

                        var matchingWar = settings.WarrantyTypes.FirstOrDefault(w => w.Name.Equals(warType, StringComparison.OrdinalIgnoreCase));
                        if (matchingWar == null)
                        {
                            war.WarrantyTypeName = DefaultWarrantyType;
                        }
                        else
                        {
                            war.WarrantyTypeName = matchingWar.Name;
                        }
                        
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

                    record.CalculatedCommission = _mainVm.CommissionProc.CalculateLineItem(record, settings.ActiveRules);

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
        [ObservableProperty] private string _category = string.Empty;
        [ObservableProperty] private double _value;
        [ObservableProperty] private string _valueText = string.Empty;
        [ObservableProperty] private double _percentage;
        [ObservableProperty] private string _color = "#3B82F6";
    }

    public class CategoryDisplay
    {
        public string Name { get; } = string.Empty;
        public string ValueText { get; } = string.Empty;
        public CategoryDisplay(string name, string valText)
        {
            Name = name;
            ValueText = valText;
        }
    }

    public class ChartItem
    {
        public TrendBucket Bucket { get; }
        public double Value { get; }
        public double Height { get; }
        public string Label => Bucket.Label;
        public string ValueText { get; } = string.Empty;
        public List<CategoryDisplay> CategoriesBreakdown { get; } = new();

        public string TotalSalesText => Bucket.TotalRevenue.ToString("C2");
        public string TotalUnitsText => $"{Bucket.TotalQuantity} units";
        public string TotalCommissionText => Bucket.TotalCommission.ToString("C2");
        public string HighestSaleText => Bucket.MaxSalePrice.ToString("C2");

        public ChartItem(TrendBucket bucket, double value, double height, string metricType)
        {
            Bucket = bucket ?? throw new ArgumentNullException(nameof(bucket));
            Value = value;
            Height = height;

            ValueText = metricType switch
            {
                "Revenue" => value.ToString("C0"),
                "Quantity" => $"{value} units",
                "Commission" => value.ToString("C0"),
                _ => value.ToString("N0")
            };

            foreach (var kvp in bucket.CategoryBreakdown)
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

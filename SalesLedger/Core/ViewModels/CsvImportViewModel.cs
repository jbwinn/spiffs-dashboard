using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SalesLedger.Core.Models;
using SalesLedger.Core.Services;

namespace SalesLedger.Core.ViewModels
{
    /// <summary>
    /// CSV import wizard: 1) pick a file and map columns, 2) review rows (fix problems, decide on possible
    /// duplicates), 3) import. Nothing is saved until the Import button is pressed.
    /// </summary>
    public partial class CsvImportViewModel : ObservableObject
    {
        private const string NoneOption = "[None]";

        private readonly LiteDbService _liteDb;
        private readonly CsvImportService _importService;
        private readonly SkuCheckService _skuCheck;
        private readonly Action _onImported;

        private CsvParseResult? _parseResult;
        private CsvColumnMapping _mapping = new();
        private CsvImportOptions _options = new();
        private UserSettings _settings = new();
        private readonly List<ImportRowViewModel> _allRows = [];

        public CsvImportViewModel(LiteDbService liteDb, CsvImportService importService, SkuCheckService skuCheck, Action onImported)
        {
            _liteDb = liteDb;
            _importService = importService;
            _skuCheck = skuCheck;
            _onImported = onImported;
        }

        // ------------------------------------------------------------------
        // Step state
        // ------------------------------------------------------------------

        [ObservableProperty] public partial bool IsVisible { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsMappingStep))]
        [NotifyPropertyChangedFor(nameof(IsReviewStep))]
        [NotifyPropertyChangedFor(nameof(IsDoneStep))]
        public partial int Step { get; set; } // 0 = mapping, 1 = review, 2 = done

        public bool IsMappingStep => Step == 0;
        public bool IsReviewStep => Step == 1;
        public bool IsDoneStep => Step == 2;

        // ------------------------------------------------------------------
        // Step 1: file + mapping
        // ------------------------------------------------------------------

        [ObservableProperty] public partial string CsvFilePath { get; set; } = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsWarrantyImport))]
        [NotifyPropertyChangedFor(nameof(IsStandardOrEbayImport))]
        public partial string ImportAsType { get; set; } = "Standard"; // "Standard", "Warranty", or "Ebay"

        public bool IsWarrantyImport => ImportAsType == "Warranty";
        public bool IsStandardOrEbayImport => ImportAsType is "Standard" or "Ebay";

        public List<string> ImportTypeOptions { get; } = ["Standard", "Warranty", "Ebay"];
        public ObservableCollection<string> CsvHeaders { get; } = [];

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasHeaders))]
        public partial int HeaderCount { get; set; }

        public bool HasHeaders => HeaderCount > 0;

        [ObservableProperty] public partial string SelectedDateHeader { get; set; } = NoneOption;
        [ObservableProperty] public partial string SelectedInvoiceHeader { get; set; } = NoneOption;
        [ObservableProperty] public partial string SelectedSkuHeader { get; set; } = NoneOption;
        [ObservableProperty] public partial string SelectedProductNameHeader { get; set; } = NoneOption;
        [ObservableProperty] public partial string SelectedCategoryHeader { get; set; } = NoneOption;
        [ObservableProperty] public partial string SelectedPriceHeader { get; set; } = NoneOption;
        [ObservableProperty] public partial string SelectedIsUsedHeader { get; set; } = NoneOption;
        [ObservableProperty] public partial string SelectedWarrantyTypeHeader { get; set; } = NoneOption;
        [ObservableProperty] public partial string SelectedWholesalePriceHeader { get; set; } = NoneOption;

        [ObservableProperty] public partial string DefaultCategory { get; set; } = string.Empty;
        [ObservableProperty] public partial string DefaultWarrantyType { get; set; } = string.Empty;
        [ObservableProperty] public partial bool AllStandardAreUsed { get; set; } = true;

        [ObservableProperty] public partial List<string> Categories { get; set; } = [];
        [ObservableProperty] public partial List<string> WarrantyTypes { get; set; } = [];

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasMappingError))]
        public partial string MappingError { get; set; } = string.Empty;

        public bool HasMappingError => !string.IsNullOrEmpty(MappingError);

        // ------------------------------------------------------------------
        // Step 2: review
        // ------------------------------------------------------------------

        public ObservableCollection<ImportRowViewModel> ProblemRows { get; } = [];
        public ObservableCollection<ImportRowViewModel> DuplicateRows { get; } = [];
        public ObservableCollection<ImportRowViewModel> ReadyRows { get; } = [];
        public ObservableCollection<string> Warnings { get; } = [];
        public ObservableCollection<string> SkippedRows { get; } = [];

        [ObservableProperty] public partial string ReviewSummaryText { get; set; } = string.Empty;
        [ObservableProperty] public partial string ImportButtonText { get; set; } = "Import";

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
        public partial int ImportCount { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasUnresolvedProblems))]
        public partial int UnresolvedProblemCount { get; set; }

        public bool HasUnresolvedProblems => UnresolvedProblemCount > 0;
        public bool HasProblemRows => ProblemRows.Count > 0;
        public bool HasDuplicateRows => DuplicateRows.Count > 0;
        public bool HasReadyRows => ReadyRows.Count > 0;
        public bool HasWarnings => Warnings.Count > 0;
        public bool HasSkippedRows => SkippedRows.Count > 0;

        // ------------------------------------------------------------------
        // Step 3: done
        // ------------------------------------------------------------------

        [ObservableProperty] public partial string ResultText { get; set; } = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
        public partial string StatusMessage { get; set; } = string.Empty;

        public bool HasStatusMessage => !string.IsNullOrEmpty(StatusMessage);

        // ------------------------------------------------------------------
        // Commands
        // ------------------------------------------------------------------

        public void Open()
        {
            var settings = _liteDb.GetUserSettings();
            Categories = (settings.ProductCategories ?? []).Where(c => c.IsActive).Select(c => c.Name).ToList();
            WarrantyTypes = (settings.WarrantyTypes ?? []).Where(w => w.IsActive).Select(w => w.Name).ToList();

            CsvFilePath = string.Empty;
            CsvHeaders.Clear();
            HeaderCount = 0;
            ImportAsType = "Standard";
            ResetMappingSelections();
            DefaultCategory = Categories.FirstOrDefault() ?? string.Empty;
            DefaultWarrantyType = WarrantyTypes.FirstOrDefault() ?? string.Empty;
            AllStandardAreUsed = true;
            MappingError = string.Empty;
            StatusMessage = string.Empty;
            ClearReview();

            Step = 0;
            IsVisible = true;
        }

        [RelayCommand]
        private void Close()
        {
            IsVisible = false;
            ClearReview();
        }

        [RelayCommand]
        private async Task BrowseCsvFile()
        {
            var storage = GetStorageProvider();
            if (storage == null) return;

            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select Spiff Sheet CSV File",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("CSV Files") { Patterns = ["*.csv"], MimeTypes = ["text/csv", "text/plain"] }]
            });

            if (files is { Count: > 0 })
            {
                CsvFilePath = files[0].Path.LocalPath;
                LoadHeaders();
            }
        }

        private void LoadHeaders()
        {
            MappingError = string.Empty;
            CsvHeaders.Clear();
            HeaderCount = 0;
            ResetMappingSelections();

            try
            {
                var rows = CsvHelper.ParseCsv(CsvFilePath);
                if (rows.Count == 0)
                {
                    MappingError = "The file is empty.";
                    return;
                }

                var headers = rows[0];
                CsvHeaders.Add(NoneOption);
                foreach (var h in headers)
                {
                    CsvHeaders.Add(h);
                }
                HeaderCount = headers.Length;

                SelectedDateHeader = AutoMatchHeader(headers, "date", "trans");
                SelectedInvoiceHeader = AutoMatchHeader(headers, "receipt", "invoice", "order", "number");
                SelectedSkuHeader = AutoMatchHeader(headers, "sku", "product sku", "item");
                SelectedProductNameHeader = AutoMatchHeader(headers, "product name", "name", "description");
                SelectedCategoryHeader = AutoMatchHeader(headers, "category", "product type", "type");
                SelectedPriceHeader = AutoMatchHeader(headers, "sale price", "price", "product price");
                SelectedIsUsedHeader = AutoMatchHeader(headers, "used", "is used", "gear", "condition");
                SelectedWarrantyTypeHeader = AutoMatchHeader(headers, "warranty type", "plan");
                SelectedWholesalePriceHeader = AutoMatchHeader(headers, "wholesale", "canon unit", "cost");
            }
            catch (Exception ex)
            {
                MappingError = $"Couldn't read the file: {ex.Message}";
            }
        }

        [RelayCommand]
        private void Review()
        {
            MappingError = string.Empty;

            if (string.IsNullOrEmpty(CsvFilePath) || HeaderCount == 0)
            {
                MappingError = "Choose a CSV file first.";
                return;
            }
            if (IsNone(SelectedPriceHeader))
            {
                MappingError = "Map the Sale Price column before reviewing.";
                return;
            }
            if (IsNone(SelectedSkuHeader) && IsNone(SelectedProductNameHeader))
            {
                MappingError = "Map a Product SKU or Product Name column before reviewing.";
                return;
            }

            List<string[]> csvRows;
            try
            {
                csvRows = CsvHelper.ParseCsv(CsvFilePath);
            }
            catch (Exception ex)
            {
                MappingError = $"Couldn't read the file: {ex.Message}";
                return;
            }

            bool isWarranty = IsWarrantyImport;
            _mapping = new CsvColumnMapping
            {
                Date = ToHeader(SelectedDateHeader),
                Invoice = ToHeader(SelectedInvoiceHeader),
                Sku = ToHeader(SelectedSkuHeader),
                ProductName = ToHeader(SelectedProductNameHeader),
                Category = isWarranty ? null : ToHeader(SelectedCategoryHeader),
                Price = ToHeader(SelectedPriceHeader),
                IsUsed = isWarranty ? null : ToHeader(SelectedIsUsedHeader),
                WarrantyType = isWarranty ? ToHeader(SelectedWarrantyTypeHeader) : null,
                WholesalePrice = isWarranty ? ToHeader(SelectedWholesalePriceHeader) : null
            };

            _options = new CsvImportOptions
            {
                ImportType = ImportAsType switch { "Warranty" => SaleType.Warranty, "Ebay" => SaleType.Ebay, _ => SaleType.Standard },
                DefaultCategory = DefaultCategory,
                DefaultWarrantyType = DefaultWarrantyType,
                TreatUnmarkedAsUsed = AllStandardAreUsed,
                ImportTime = DateTime.Now,
                RequireDate = _mapping.Date != null,
                RequireWholesalePrice = _mapping.WholesalePrice != null
            };

            _settings = _liteDb.GetUserSettings();
            _parseResult = _importService.Parse(csvRows, _mapping, _options);

            BuildReview();
            Step = 1;
        }

        private void BuildReview()
        {
            ClearReview();

            foreach (var warning in _parseResult!.Warnings) Warnings.Add(warning);
            foreach (var skipped in _parseResult.Skipped) SkippedRows.Add($"Row {skipped.RowNumber}: {skipped.Reason}");

            foreach (var row in _parseResult.Rows)
            {
                var rowVm = new ImportRowViewModel(row, IsWarrantyImport, Categories, WarrantyTypes, Revalidate, RefreshCounts);
                _allRows.Add(rowVm);

                // Sort into sections once, based on the file as loaded. Rows stay in their section while being fixed.
                if (!rowVm.IsValid) ProblemRows.Add(rowVm);
                else if (rowVm.IsDuplicate) DuplicateRows.Add(rowVm);
                else ReadyRows.Add(rowVm);
            }

            OnPropertyChanged(nameof(HasProblemRows));
            OnPropertyChanged(nameof(HasDuplicateRows));
            OnPropertyChanged(nameof(HasReadyRows));
            OnPropertyChanged(nameof(HasWarnings));
            OnPropertyChanged(nameof(HasSkippedRows));
            RefreshCounts();
        }

        /// <summary>
        /// Validates a row and, if it's valid, checks it against the ledger: first for the same transaction,
        /// then (for used gear) for a SKU that has already been sold. Either is flagged for review, not rejected.
        /// </summary>
        private (CsvRowValidation Validation, string? Flag) Revalidate(CsvImportRow row)
        {
            var validation = _importService.Validate(row, _options, _settings);
            if (validation.Record is not { } record)
            {
                return (validation, null);
            }

            if (_importService.FindPossibleDuplicate(record, validation.InvoiceGenerated) is { } duplicate)
            {
                return (validation, $"Possible duplicate of {duplicate.TransactionDate:yyyy-MM-dd} · {duplicate.InvoiceNumber} · " +
                                    $"{duplicate.ProductName} · {duplicate.SalePrice:C} ({duplicate.Status})");
            }

            if (SkuCheckService.ShouldCheck(record))
            {
                var priorSales = _skuCheck.FindPriorSales(record.Sku);
                if (SkuCheckService.BuildWarning(record.Sku, priorSales) is { } warning)
                {
                    var lead = warning.IsResaleOfReturnedItem
                        ? $"SKU {record.Sku} was returned and is being sold again"
                        : $"Duplicate SKU {record.Sku}, already on a sale that wasn't returned";
                    return (validation, $"{lead}: {string.Join("; ", priorSales.Select(m => m.Description))}");
                }
            }

            return (validation, null);
        }

        private void RefreshCounts()
        {
            ImportCount = _allRows.Count(r => r.IsIncluded && !r.IsImported);
            UnresolvedProblemCount = _allRows.Count(r => !r.IsValid && !r.IsImported);

            int duplicatesSkipped = _allRows.Count(r => r.IsValid && r.IsDuplicate && !r.ImportAnyway);
            var parts = new List<string> { $"{ImportCount} ready to import" };
            if (UnresolvedProblemCount > 0) parts.Add($"{UnresolvedProblemCount} need fixing");
            if (duplicatesSkipped > 0) parts.Add($"{duplicatesSkipped} possible duplicate(s) will be skipped");
            if (_parseResult?.Skipped.Count > 0) parts.Add($"{_parseResult.Skipped.Count} row(s) ignored");
            ReviewSummaryText = string.Join(" · ", parts);
            ImportButtonText = ImportCount == 1 ? "Import 1 Row" : $"Import {ImportCount} Rows";
        }

        [RelayCommand]
        private void Back()
        {
            ClearReview();
            Step = 0;
        }

        [RelayCommand]
        private void ImportAllDuplicates()
        {
            foreach (var row in DuplicateRows) row.ImportAnyway = true;
        }

        [RelayCommand]
        private void SkipAllDuplicates()
        {
            foreach (var row in DuplicateRows) row.ImportAnyway = false;
        }

        private bool CanImport() => ImportCount > 0;

        [RelayCommand(CanExecute = nameof(CanImport))]
        private void Import()
        {
            StatusMessage = string.Empty;
            var toImport = _allRows.Where(r => r.IsIncluded && !r.IsImported).ToList();

            try
            {
                var imported = _importService.Import(toImport.Select(r => r.Record!).ToList());
                foreach (var row in toImport) row.IsImported = true;

                int problems = _allRows.Count(r => !r.IsValid);
                int duplicatesSkipped = _allRows.Count(r => r.IsValid && r.IsDuplicate && !r.ImportAnyway);

                var lines = new List<string> { imported == 1 ? "Imported 1 row." : $"Imported {imported} rows." };
                if (duplicatesSkipped > 0) lines.Add($"Skipped {duplicatesSkipped} possible duplicate(s).");
                if (problems > 0) lines.Add($"{problems} row(s) with problems were not imported. Save them as a CSV, fix them, and import that file.");
                ResultText = string.Join(" ", lines);

                RefreshCounts();
                Step = 2;
                _onImported();
            }
            catch (Exception ex)
            {
                // The import runs in a single transaction, so nothing was saved
                StatusMessage = $"Import failed and nothing was saved: {ex.Message}";
            }
        }

        [RelayCommand]
        private async Task SaveProblemRows()
        {
            StatusMessage = string.Empty;
            var problems = _allRows.Where(r => !r.IsValid && !r.IsImported).ToList();
            if (problems.Count == 0 || _parseResult == null) return;

            var storage = GetStorageProvider();
            if (storage == null) return;

            var suggestedName = $"{Path.GetFileNameWithoutExtension(CsvFilePath)}_problem_rows.csv";
            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save Problem Rows",
                SuggestedFileName = suggestedName,
                DefaultExtension = "csv",
                FileTypeChoices = [new FilePickerFileType("CSV Files") { Patterns = ["*.csv"] }]
            });

            if (file == null) return;

            try
            {
                CsvImportService.WriteRows(file.Path.LocalPath, _parseResult.Headers, _mapping,
                    problems.Select(r => (r.Row, r.ErrorText)));
                StatusMessage = $"Saved {problems.Count} problem row(s) to {file.Path.LocalPath}. Fix them, then import that file.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Couldn't save the file: {ex.Message}";
            }
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private void ClearReview()
        {
            _allRows.Clear();
            ProblemRows.Clear();
            DuplicateRows.Clear();
            ReadyRows.Clear();
            Warnings.Clear();
            SkippedRows.Clear();
            ImportCount = 0;
            UnresolvedProblemCount = 0;
            ReviewSummaryText = string.Empty;
            ResultText = string.Empty;
        }

        private void ResetMappingSelections()
        {
            SelectedDateHeader = NoneOption;
            SelectedInvoiceHeader = NoneOption;
            SelectedSkuHeader = NoneOption;
            SelectedProductNameHeader = NoneOption;
            SelectedCategoryHeader = NoneOption;
            SelectedPriceHeader = NoneOption;
            SelectedIsUsedHeader = NoneOption;
            SelectedWarrantyTypeHeader = NoneOption;
            SelectedWholesalePriceHeader = NoneOption;
        }

        private static bool IsNone(string? header) => string.IsNullOrEmpty(header) || header == NoneOption;
        private static string? ToHeader(string? header) => IsNone(header) ? null : header;

        private static string AutoMatchHeader(string[] headers, params string[] keywords)
        {
            // Prefer keywords in the order given, so "sale price" beats a looser "price" match
            foreach (var kw in keywords)
            {
                foreach (var h in headers)
                {
                    if (h.ToLowerInvariant().Contains(kw)) return h;
                }
            }
            return NoneOption;
        }

        private static IStorageProvider? GetStorageProvider() =>
            Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: not null } desktop
                ? desktop.MainWindow.StorageProvider
                : null;
    }

    /// <summary>One CSV row in the review step. Editing a field re-checks the row immediately.</summary>
    public partial class ImportRowViewModel : ObservableObject
    {
        private readonly Func<CsvImportRow, (CsvRowValidation Validation, string? Flag)> _revalidate;
        private readonly Action _onChanged;

        public ImportRowViewModel(
            CsvImportRow row,
            bool isWarranty,
            List<string> categoryOptions,
            List<string> warrantyTypeOptions,
            Func<CsvImportRow, (CsvRowValidation, string?)> revalidate,
            Action onChanged)
        {
            Row = row;
            IsWarranty = isWarranty;
            CategoryOptions = categoryOptions;
            WarrantyTypeOptions = warrantyTypeOptions;
            _revalidate = revalidate;
            _onChanged = onChanged;

            Date = row.Date;
            Invoice = row.Invoice;
            Sku = row.Sku;
            ProductName = row.ProductName;
            Category = row.Category;
            Price = row.Price;
            WarrantyType = row.WarrantyType;
            WholesalePrice = row.WholesalePrice;

            Recheck(notify: false);
        }

        public CsvImportRow Row { get; }
        public bool IsWarranty { get; }
        public bool IsStandardOrEbay => !IsWarranty;
        public List<string> CategoryOptions { get; }
        public List<string> WarrantyTypeOptions { get; }
        public string RowLabel => $"Row {Row.RowNumber}";

        [ObservableProperty] public partial string Date { get; set; } = string.Empty;
        [ObservableProperty] public partial string Invoice { get; set; } = string.Empty;
        [ObservableProperty] public partial string Sku { get; set; } = string.Empty;
        [ObservableProperty] public partial string ProductName { get; set; } = string.Empty;
        [ObservableProperty] public partial string Category { get; set; } = string.Empty;
        [ObservableProperty] public partial string Price { get; set; } = string.Empty;
        [ObservableProperty] public partial string WarrantyType { get; set; } = string.Empty;
        [ObservableProperty] public partial string WholesalePrice { get; set; } = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsValid))]
        [NotifyPropertyChangedFor(nameof(IsIncluded))]
        [NotifyPropertyChangedFor(nameof(IsFixed))]
        public partial SaleRecord? Record { get; private set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasErrors))]
        public partial string ErrorText { get; private set; } = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsDuplicate))]
        [NotifyPropertyChangedFor(nameof(IsIncluded))]
        public partial string DuplicateText { get; private set; } = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsIncluded))]
        public partial bool ImportAnyway { get; set; }

        [ObservableProperty] public partial bool IsImported { get; set; }

        public bool IsValid => Record != null;
        public bool HasErrors => !string.IsNullOrEmpty(ErrorText);
        public bool IsDuplicate => !string.IsNullOrEmpty(DuplicateText);
        public bool IsIncluded => IsValid && (!IsDuplicate || ImportAnyway);

        /// <summary>True once a row that started with problems has been corrected.</summary>
        public bool IsFixed => IsValid && _startedInvalid;

        private bool _startedInvalid;

        public string SummaryText =>
            $"{Date} · {(string.IsNullOrEmpty(Invoice) ? "(no invoice)" : Invoice)} · {Sku} · {ProductName} · {Price}";

        // Pick-lists that fill in the text fields. Bound separately so a value that isn't in the list
        // (e.g. "Lenses") stays visible as typed instead of being blanked out by the ComboBox.
        [ObservableProperty] public partial string? CategoryChoice { get; set; }
        [ObservableProperty] public partial string? WarrantyTypeChoice { get; set; }

        partial void OnCategoryChoiceChanged(string? value) { if (!string.IsNullOrEmpty(value)) Category = value; }
        partial void OnWarrantyTypeChoiceChanged(string? value) { if (!string.IsNullOrEmpty(value)) WarrantyType = value; }

        partial void OnDateChanged(string value) { Row.Date = value; Recheck(); }
        partial void OnInvoiceChanged(string value) { Row.Invoice = value; Recheck(); }
        partial void OnSkuChanged(string value) { Row.Sku = value; Recheck(); }
        partial void OnProductNameChanged(string value) { Row.ProductName = value; Recheck(); }
        partial void OnCategoryChanged(string value) { Row.Category = value ?? string.Empty; Recheck(); }
        partial void OnPriceChanged(string value) { Row.Price = value; Recheck(); }
        partial void OnWarrantyTypeChanged(string value) { Row.WarrantyType = value ?? string.Empty; Recheck(); }
        partial void OnWholesalePriceChanged(string value) { Row.WholesalePrice = value; Recheck(); }
        partial void OnImportAnywayChanged(bool value) => _onChanged();

        private bool _initialized;

        private void Recheck(bool notify = true)
        {
            // Field setters in the constructor fire before the delegates are usable
            if (!_initialized && notify) return;

            var (validation, flag) = _revalidate(Row);
            Record = validation.Record;
            ErrorText = string.Join("; ", validation.Errors);
            DuplicateText = flag ?? string.Empty;

            if (!_initialized)
            {
                _startedInvalid = !IsValid;
                _initialized = true;
            }

            OnPropertyChanged(nameof(SummaryText));
            if (notify) _onChanged();
        }
    }
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SalesLedger.Core.Models;

namespace SalesLedger.Core.ViewModels
{
    public partial class FullScreenSettingsViewModel : ObservableObject
    {
        private readonly MainWindowViewModel _mainVm;

        [ObservableProperty]
        public partial string UserDisplayName { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string NewCategoryName { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string NewWarrantyTypeName { get; set; } = string.Empty;

        // Collections bound to UI
        public ObservableCollection<AppCategory> Categories { get; } = [];
        public ObservableCollection<AppWarrantyType> WarrantyTypes { get; } = [];
        public ObservableCollection<CommissionRule> Rules { get; } = [];

        // New Rule fields
        [ObservableProperty] public partial string NewRuleName { get; set; } = string.Empty;
        [ObservableProperty] public partial RuleScope NewRuleScope { get; set; } = RuleScope.CategorySpecific;
        [ObservableProperty] public partial string NewRuleTargetCategory { get; set; } = string.Empty;
        [ObservableProperty] public partial PayoutType NewRuleCalculationType { get; set; } = PayoutType.PercentageOfPrice;
        [ObservableProperty] public partial decimal NewRuleValue { get; set; }

        // Edit Rule fields
        [ObservableProperty] public partial bool IsEditRuleDialogVisible { get; set; }
        [ObservableProperty] public partial string EditRuleName { get; set; } = string.Empty;
        [ObservableProperty] public partial RuleScope EditRuleScope { get; set; } = RuleScope.CategorySpecific;
        [ObservableProperty] public partial string EditRuleTargetCategory { get; set; } = string.Empty;
        [ObservableProperty] public partial PayoutType EditRuleCalculationType { get; set; } = PayoutType.PercentageOfPrice;
        [ObservableProperty] public partial decimal EditRuleValue { get; set; }
        private CommissionRule? _editingRuleInstance;

        // Binding helper for rule scopes & payout types
        public List<RuleScope> ScopeOptions => Enum.GetValues<RuleScope>().ToList();
        public List<PayoutType> PayoutTypeOptions => Enum.GetValues<PayoutType>().ToList();
        public List<string> ActiveCategoryNames => Categories.Where(c => c.IsActive).Select(c => c.Name).ToList();

#if DEBUG
        public bool IsDebugMode => true;
#else
        public bool IsDebugMode => false;
#endif

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
        public partial string StatusMessage { get; set; } = string.Empty;

        public bool HasStatusMessage => !string.IsNullOrEmpty(StatusMessage);

        [ObservableProperty]
        public partial bool IsStatusError { get; set; }

        [ObservableProperty]
        public partial bool AutoUpdateEnabled { get; set; }

        [ObservableProperty]
        public partial string UpdateStatusMessage { get; set; } = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsNotCheckingForUpdates))]
        private partial bool IsCheckingForUpdates { get; set; }

        public bool IsNotCheckingForUpdates => !IsCheckingForUpdates;

        public FullScreenSettingsViewModel(MainWindowViewModel mainVm)
        {
            _mainVm = mainVm ?? throw new ArgumentNullException(nameof(mainVm));
            LoadSettings();
        }

        public void LoadSettings()
        {
            var settings = _mainVm.LiteDb.GetUserSettings();
            UserDisplayName = settings.UserDisplayName;
            AutoUpdateEnabled = settings.AutoUpdateEnabled;

            Categories.Clear();
            foreach (var cat in settings.ProductCategories ?? [])
            {
                Categories.Add(cat);
            }

            WarrantyTypes.Clear();
            foreach (var wt in settings.WarrantyTypes ?? [])
            {
                WarrantyTypes.Add(wt);
            }

            RefreshRulesList(settings.ActiveRules ?? []);
        }

        private void RefreshRulesList(List<CommissionRule> activeRules)
        {
            Rules.Clear();
            var sorted = activeRules.OrderBy(r => r.PriorityOrder).ToList();
            for (int i = 0; i < sorted.Count; i++)
            {
                sorted[i].PriorityOrder = i; // Enforce clean 0-indexed ranking
                Rules.Add(sorted[i]);
            }
        }

        [RelayCommand]
        private void SaveProfile()
        {
            if (string.IsNullOrWhiteSpace(UserDisplayName))
            {
                ShowStatus("User Display Name cannot be empty.", true);
                return;
            }

            var settings = _mainVm.LiteDb.GetUserSettings();
            settings.UserDisplayName = UserDisplayName;
            settings.AutoUpdateEnabled = AutoUpdateEnabled;
            _mainVm.LiteDb.SaveUserSettings(settings);
            ShowStatus("Profile saved successfully!", false);
        }

        [RelayCommand]
        private async Task CheckForUpdatesManual()
        {
            if (IsCheckingForUpdates) return;
            IsCheckingForUpdates = true;
            UpdateStatusMessage = "Checking for updates...";

            try
            {
                var updateSource = new Velopack.Sources.GithubSource("https://github.com/jbwinn/spiffs-dashboard", null, false);
                var updateManager = new Velopack.UpdateManager(updateSource);
                var updateInfo = await updateManager.CheckForUpdatesAsync();

                if (updateInfo != null)
                {
                    UpdateStatusMessage = $"Update found (v{updateInfo.TargetFullRelease.Version}). Dialog opened.";
                    _mainVm.UpdateManager = updateManager;
                    _mainVm.UpdateInfo = updateInfo;
                    _mainVm.UpdateMessageText = $"A new version (v{updateInfo.TargetFullRelease.Version}) is available. Would you like to install it now? The application will restart automatically.";
                    _mainVm.IsUpdateDialogVisible = true;
                }
                else
                {
                    UpdateStatusMessage = "Application is up to date.";
                }
            }
            catch (Exception ex)
            {
                UpdateStatusMessage = $"Update check failed: {ex.Message}";
            }
            finally
            {
                IsCheckingForUpdates = false;
            }
        }

        [RelayCommand]
        private void TestUpdateUi()
        {
            _mainVm.UpdateMessageText = "A new version (v2.0.1-mock) is available. Would you like to install it now? The application will restart automatically.";
            _mainVm.IsUpdateDialogVisible = true;
        }

        [RelayCommand]
        private void ResetDatabase()
        {
            try
            {
                // Delete all records from LiteDB collections
                _mainVm.LiteDb.Sales.DeleteAll();
                _mainVm.LiteDb.Settings.DeleteAll();
                _mainVm.LiteDb.Reports.DeleteAll();

                // Re-initialize default settings
                LoadSettings();

#if DEBUG
                // Auto-populate generic products in debug mode
                _mainVm.LiteDb.SeedDebugData(force: true);
#endif

                // Trigger a DuckDB sync rebuild
                _mainVm.Sync.QueueRebuild();

                ShowStatus("Database has been reset to defaults. All records cleared.", false);
            }
            catch (Exception ex)
            {
                ShowStatus($"Error resetting database: {ex.Message}", true);
            }
        }

        [RelayCommand]
        private void AddCategory()
        {
            if (string.IsNullOrWhiteSpace(NewCategoryName)) return;

            var name = NewCategoryName.Trim();
            if (Categories.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                ShowStatus("Category already exists.", true);
                return;
            }

            var newCat = new AppCategory { Name = name, IsSystemPreset = false, IsActive = true };
            Categories.Add(newCat);
            SaveTaxonomyAndRules();
            NewCategoryName = string.Empty;
            ShowStatus($"Category '{name}' added.", false);
        }

        [RelayCommand]
        private void ToggleCategoryActive(AppCategory category)
        {
            if (category.IsSystemPreset)
            {
                ShowStatus("Cannot deactivate system preset categories.", true);
                return;
            }

            category.IsActive = !category.IsActive;
            SaveTaxonomyAndRules();
            OnPropertyChanged(nameof(ActiveCategoryNames));
            ShowStatus($"Category '{category.Name}' status updated.", false);
        }

        [RelayCommand]
        private void DeleteCategory(AppCategory category)
        {
            if (category.IsSystemPreset)
            {
                ShowStatus("Cannot delete system preset categories.", true);
                return;
            }

            // Referential Block Check
            bool isUsed = _mainVm.LiteDb.Sales.Exists(x => x.Category == category.Name);
            if (isUsed)
            {
                // Referential block triggered: shift to soft-deactivation instead
                category.IsActive = false;
                ShowStatus($"Category '{category.Name}' is referenced in transactions. Soft-deactivated instead of deleted.", true);
            }
            else
            {
                Categories.Remove(category);
                ShowStatus($"Category '{category.Name}' deleted.", false);
            }

            SaveTaxonomyAndRules();
            OnPropertyChanged(nameof(ActiveCategoryNames));
        }

        [RelayCommand]
        private void AddWarrantyType()
        {
            if (string.IsNullOrWhiteSpace(NewWarrantyTypeName)) return;

            var name = NewWarrantyTypeName.Trim();
            if (WarrantyTypes.Any(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                ShowStatus("Warranty type already exists.", true);
                return;
            }

            var newWt = new AppWarrantyType { Name = name, IsSystemPreset = false, IsActive = true };
            WarrantyTypes.Add(newWt);
            SaveTaxonomyAndRules();
            NewWarrantyTypeName = string.Empty;
            ShowStatus($"Warranty type '{name}' added.", false);
        }

        [RelayCommand]
        private void ToggleWarrantyTypeActive(AppWarrantyType warrantyType)
        {
            if (warrantyType.IsSystemPreset)
            {
                ShowStatus("Cannot deactivate system preset warranty types.", true);
                return;
            }

            warrantyType.IsActive = !warrantyType.IsActive;
            SaveTaxonomyAndRules();
            ShowStatus($"Warranty type '{warrantyType.Name}' status updated.", false);
        }

        [RelayCommand]
        private void DeleteWarrantyType(AppWarrantyType warrantyType)
        {
            if (warrantyType.IsSystemPreset)
            {
                ShowStatus("Cannot delete system preset warranty types.", true);
                return;
            }

            // Referential check
            bool isUsed = _mainVm.LiteDb.Sales.Exists(x => x.RecordType == SaleType.Warranty && ((WarrantySale)x).WarrantyTypeName == warrantyType.Name);
            if (isUsed)
            {
                warrantyType.IsActive = false;
                ShowStatus($"Warranty '{warrantyType.Name}' is referenced in transactions. Soft-deactivated instead.", true);
            }
            else
            {
                WarrantyTypes.Remove(warrantyType);
                ShowStatus($"Warranty '{warrantyType.Name}' deleted.", false);
            }

            SaveTaxonomyAndRules();
        }

        [RelayCommand]
        private void AddCommissionRule()
        {
            if (string.IsNullOrWhiteSpace(NewRuleName))
            {
                ShowStatus("Rule Name cannot be empty.", true);
                return;
            }

            if (NewRuleScope == RuleScope.CategorySpecific && string.IsNullOrEmpty(NewRuleTargetCategory))
            {
                ShowStatus("Please select a target category for CategorySpecific rule.", true);
                return;
            }

            var newRule = new CommissionRule
            {
                RuleName = NewRuleName.Trim(),
                Scope = NewRuleScope,
                TargetCategory = NewRuleScope == RuleScope.CategorySpecific ? NewRuleTargetCategory : string.Empty,
                CalculationType = NewRuleCalculationType,
                RuleValue = NewRuleValue,
                PriorityOrder = Rules.Count
            };

            Rules.Add(newRule);
            SaveTaxonomyAndRules();

            // Clear inputs
            NewRuleName = string.Empty;
            NewRuleValue = 0m;
            ShowStatus("Rule added to waterfall.", false);
        }

        [RelayCommand]
        private void EditCommissionRule(CommissionRule? rule)
        {
            if (rule == null) return;
            _editingRuleInstance = rule;
            EditRuleName = rule.RuleName;
            EditRuleScope = rule.Scope;
            EditRuleTargetCategory = rule.TargetCategory;
            EditRuleCalculationType = rule.CalculationType;
            EditRuleValue = rule.RuleValue;
            IsEditRuleDialogVisible = true;
        }

        [RelayCommand]
        private void CloseEditRuleDialog()
        {
            IsEditRuleDialogVisible = false;
            _editingRuleInstance = null;
        }

        [RelayCommand]
        private void SaveEditedRule()
        {
            if (_editingRuleInstance == null) return;

            if (string.IsNullOrWhiteSpace(EditRuleName))
            {
                ShowStatus("Rule Name cannot be empty.", true);
                return;
            }

            if (EditRuleScope == RuleScope.CategorySpecific && string.IsNullOrEmpty(EditRuleTargetCategory))
            {
                ShowStatus("Please select a target category for CategorySpecific rule.", true);
                return;
            }

            // Update instance
            _editingRuleInstance.RuleName = EditRuleName.Trim();
            _editingRuleInstance.Scope = EditRuleScope;
            _editingRuleInstance.TargetCategory = EditRuleScope == RuleScope.CategorySpecific ? EditRuleTargetCategory : string.Empty;
            _editingRuleInstance.CalculationType = EditRuleCalculationType;
            _editingRuleInstance.RuleValue = EditRuleValue;

            // Find in collection and force a refresh by replacing it
            int index = Rules.IndexOf(_editingRuleInstance);
            if (index >= 0)
            {
                Rules[index] = _editingRuleInstance;
            }

            SaveTaxonomyAndRules();
            IsEditRuleDialogVisible = false;
            _editingRuleInstance = null;
            ShowStatus("Rule edited successfully.", false);
        }

        [RelayCommand]
        private void DeleteCommissionRule(CommissionRule rule)
        {
            Rules.Remove(rule);
            
            // Re-order remaining rules
            var sorted = Rules.OrderBy(r => r.PriorityOrder).ToList();
            RefreshRulesList(sorted);
            SaveTaxonomyAndRules();
            ShowStatus("Rule removed.", false);
        }

        [RelayCommand]
        private void MoveRuleUp(CommissionRule rule)
        {
            int index = Rules.IndexOf(rule);
            if (index <= 0) return; // Already at the top

            var prev = Rules[index - 1];
            
            // Swap priority orders
            (rule.PriorityOrder, prev.PriorityOrder) = (prev.PriorityOrder, rule.PriorityOrder);

            Rules[index] = prev;
            Rules[index - 1] = rule;

            SaveTaxonomyAndRules();
        }

        [RelayCommand]
        private void MoveRuleDown(CommissionRule rule)
        {
            int index = Rules.IndexOf(rule);
            if (index < 0 || index >= Rules.Count - 1) return; // Already at the bottom

            var next = Rules[index + 1];

            // Swap priority orders
            (rule.PriorityOrder, next.PriorityOrder) = (next.PriorityOrder, rule.PriorityOrder);

            Rules[index] = next;
            Rules[index + 1] = rule;

            SaveTaxonomyAndRules();
        }

        private void SaveTaxonomyAndRules()
        {
            var settings = _mainVm.LiteDb.GetUserSettings();
            settings.ProductCategories = Categories.ToList();
            settings.WarrantyTypes = WarrantyTypes.ToList();
            settings.ActiveRules = Rules.ToList();
            _mainVm.LiteDb.SaveUserSettings(settings);

            // Rebuild DuckDB analytics to apply new rule priorities or category status shifts
            _mainVm.Sync.QueueRebuild();
        }

        private void ShowStatus(string message, bool isError)
        {
            StatusMessage = message;
            IsStatusError = isError;
        }
    }
}

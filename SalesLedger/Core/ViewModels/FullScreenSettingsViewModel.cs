using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SalesLedger.Core.Models;

namespace SalesLedger.Core.ViewModels
{
    public partial class FullScreenSettingsViewModel : ObservableObject
    {
        private readonly MainWindowViewModel _mainVm;

        [ObservableProperty]
        private string _userDisplayName = string.Empty;

        [ObservableProperty]
        private string _newCategoryName = string.Empty;

        [ObservableProperty]
        private string _newWarrantyTypeName = string.Empty;

        // Collections bound to UI
        public ObservableCollection<AppCategory> Categories { get; } = new();
        public ObservableCollection<AppWarrantyType> WarrantyTypes { get; } = new();
        public ObservableCollection<CommissionRule> Rules { get; } = new();

        // New Rule fields
        [ObservableProperty] private string _newRuleName = string.Empty;
        [ObservableProperty] private RuleScope _newRuleScope = RuleScope.CategorySpecific;
        [ObservableProperty] private string _newRuleTargetCategory = string.Empty;
        [ObservableProperty] private PayoutType _newRuleCalculationType = PayoutType.PercentageOfPrice;
        [ObservableProperty] private decimal _newRuleValue;

        // Binding helper for rule scopes & payout types
        public List<RuleScope> ScopeOptions => Enum.GetValues<RuleScope>().ToList();
        public List<PayoutType> PayoutTypeOptions => Enum.GetValues<PayoutType>().ToList();
        public List<string> ActiveCategoryNames => Categories.Where(c => c.IsActive).Select(c => c.Name).ToList();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
        private string _statusMessage = string.Empty;

        public bool HasStatusMessage => !string.IsNullOrEmpty(StatusMessage);

        [ObservableProperty]
        private bool _isStatusError;

        public FullScreenSettingsViewModel(MainWindowViewModel mainVm)
        {
            _mainVm = mainVm ?? throw new ArgumentNullException(nameof(mainVm));
        }

        public void LoadSettings()
        {
            var settings = _mainVm.LiteDb.GetUserSettings();
            UserDisplayName = settings.UserDisplayName;

            Categories.Clear();
            foreach (var cat in settings.ProductCategories)
            {
                Categories.Add(cat);
            }

            WarrantyTypes.Clear();
            foreach (var wt in settings.WarrantyTypes)
            {
                WarrantyTypes.Add(wt);
            }

            RefreshRulesList(settings.ActiveRules);
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
        public void SaveProfile()
        {
            if (string.IsNullOrWhiteSpace(UserDisplayName))
            {
                ShowStatus("User Display Name cannot be empty.", true);
                return;
            }

            var settings = _mainVm.LiteDb.GetUserSettings();
            settings.UserDisplayName = UserDisplayName;
            _mainVm.LiteDb.SaveUserSettings(settings);
            ShowStatus("Profile saved successfully!", false);
        }

        [RelayCommand]
        public void ResetDatabase()
        {
            try
            {
                // Delete all records from LiteDB collections
                _mainVm.LiteDb.Sales.DeleteAll();
                _mainVm.LiteDb.Settings.DeleteAll();
                _mainVm.LiteDb.Reports.DeleteAll();

                // Re-initialize default settings
                var settings = _mainVm.LiteDb.GetUserSettings();
                LoadSettings();

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
        public void AddCategory()
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
        public void ToggleCategoryActive(AppCategory category)
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
        public void DeleteCategory(AppCategory category)
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
        public void AddWarrantyType()
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
        public void ToggleWarrantyTypeActive(AppWarrantyType warrantyType)
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
        public void DeleteWarrantyType(AppWarrantyType warrantyType)
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
        public void AddCommissionRule()
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
        public void DeleteCommissionRule(CommissionRule rule)
        {
            Rules.Remove(rule);
            
            // Re-order remaining rules
            var sorted = Rules.OrderBy(r => r.PriorityOrder).ToList();
            RefreshRulesList(sorted);
            SaveTaxonomyAndRules();
            ShowStatus("Rule removed.", false);
        }

        [RelayCommand]
        public void MoveRuleUp(CommissionRule rule)
        {
            int index = Rules.IndexOf(rule);
            if (index <= 0) return; // Already at the top

            var prev = Rules[index - 1];
            
            // Swap priority orders
            int temp = rule.PriorityOrder;
            rule.PriorityOrder = prev.PriorityOrder;
            prev.PriorityOrder = temp;

            Rules[index] = prev;
            Rules[index - 1] = rule;

            SaveTaxonomyAndRules();
        }

        [RelayCommand]
        public void MoveRuleDown(CommissionRule rule)
        {
            int index = Rules.IndexOf(rule);
            if (index < 0 || index >= Rules.Count - 1) return; // Already at the bottom

            var next = Rules[index + 1];

            // Swap priority orders
            int temp = rule.PriorityOrder;
            rule.PriorityOrder = next.PriorityOrder;
            next.PriorityOrder = temp;

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

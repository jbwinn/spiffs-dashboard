using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LiteDB;
using SalesLedger.Core.Models;

namespace SalesLedger.Core.Services
{
    public class LiteDbService : IDisposable
    {
        private readonly LiteDatabase _db;
        private readonly string _dbPath;

        public LiteDbService()
        {
#if DEBUG
            var folderName = "SalesLedgerDev";
#else
            var folderName = "SalesLedger";
#endif
            var appDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                folderName
            );
            Directory.CreateDirectory(appDataDir);
            _dbPath = Path.Combine(appDataDir, "salesledger.db");

            // Setup polymorphic mapping for LiteDB
            var mapper = new BsonMapper();
            mapper.Entity<SaleRecord>().Id(x => x.Id);

            _db = new LiteDatabase(_dbPath, mapper);
            InitializeDefaultSettings();
        }

        public LiteDbService(string customDbPath)
        {
            _dbPath = customDbPath;
            var directory = Path.GetDirectoryName(customDbPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var mapper = new BsonMapper();
            mapper.Entity<SaleRecord>().Id(x => x.Id);

            _db = new LiteDatabase(_dbPath, mapper);
            InitializeDefaultSettings();
        }

        public ILiteCollection<SaleRecord> Sales => _db.GetCollection<SaleRecord>("sales");
        public ILiteCollection<UserSettings> Settings => _db.GetCollection<UserSettings>("settings");
        public ILiteCollection<PayoutReport> Reports => _db.GetCollection<PayoutReport>("reports");

        public UserSettings GetUserSettings()
        {
            var settings = Settings.FindOne(Query.All());
            if (settings == null)
            {
                settings = CreateDefaultSettings();
                Settings.Insert(settings);
            }
            return settings;
        }

        public void SaveUserSettings(UserSettings settings)
        {
            Settings.Upsert(settings);
        }

        private void InitializeDefaultSettings()
        {
            var settings = Settings.FindOne(Query.All());
            if (settings == null)
            {
                var defaultSettings = CreateDefaultSettings();
                Settings.Insert(defaultSettings);
            }
            else
            {
                bool modified = false;
                
                var targetCategories = new[] { "Lens", "SLR", "Digital - SLR", "Mirrorless", "TLR", "Medium Format", "Point and Shoot", "Rangefinder", "Camcorder", "Bridge Camera", "Light Meter/Flash", "Tripod", "Converter/Extender", "Bag/Accessory" };
                var targetWarranties = new[] { "Sony", "Fuji", "Nikon", "Mack", "Canon" };

                // Reconstruct the product categories: remove existing presets, insert new presets in front, keep user custom categories
                var userCustomCategories = new List<AppCategory>();
                if (settings.ProductCategories != null)
                {
                    foreach (var c in settings.ProductCategories)
                    {
                        if (!c.IsSystemPreset)
                        {
                            userCustomCategories.Add(c);
                        }
                    }
                }

                var currentPresetNames = new List<string>();
                if (settings.ProductCategories != null)
                {
                    foreach (var c in settings.ProductCategories)
                    {
                        if (c.IsSystemPreset)
                        {
                            currentPresetNames.Add(c.Name);
                        }
                    }
                }

                // If they don't match the new list exactly
                bool categoriesMatch = currentPresetNames.Count == targetCategories.Length;
                if (categoriesMatch)
                {
                    for (int i = 0; i < targetCategories.Length; i++)
                    {
                        if (currentPresetNames[i] != targetCategories[i])
                        {
                            categoriesMatch = false;
                            break;
                        }
                    }
                }

                if (!categoriesMatch)
                {
                    var newCategories = new List<AppCategory>();
                    foreach (var name in targetCategories)
                    {
                        newCategories.Add(new AppCategory { Name = name, IsSystemPreset = true, IsActive = true });
                    }
                    newCategories.AddRange(userCustomCategories);
                    settings.ProductCategories = newCategories;
                    modified = true;
                }

                // Warranties
                var userCustomWarranties = new List<AppWarrantyType>();
                if (settings.WarrantyTypes != null)
                {
                    foreach (var w in settings.WarrantyTypes)
                    {
                        if (!w.IsSystemPreset)
                        {
                            userCustomWarranties.Add(w);
                        }
                    }
                }

                var currentPresetWarranties = new List<string>();
                if (settings.WarrantyTypes != null)
                {
                    foreach (var w in settings.WarrantyTypes)
                    {
                        if (w.IsSystemPreset)
                        {
                            currentPresetWarranties.Add(w.Name);
                        }
                    }
                }

                bool warrantiesMatch = currentPresetWarranties.Count == targetWarranties.Length;
                if (warrantiesMatch)
                {
                    for (int i = 0; i < targetWarranties.Length; i++)
                    {
                        if (currentPresetWarranties[i] != targetWarranties[i])
                        {
                            warrantiesMatch = false;
                            break;
                        }
                    }
                }

                if (!warrantiesMatch)
                {
                    var newWarranties = new List<AppWarrantyType>();
                    foreach (var name in targetWarranties)
                    {
                        newWarranties.Add(new AppWarrantyType { Name = name, IsSystemPreset = true, IsActive = true });
                    }
                    newWarranties.AddRange(userCustomWarranties);
                    settings.WarrantyTypes = newWarranties;
                    modified = true;
                }

                // Ensure default commission rules reflect Lens and Mirrorless targets if they were missing, and upgrade old defaults
                if (settings.ActiveRules != null)
                {
                    bool hasLensRule = false;
                    bool hasMirrorlessRule = false;
                    bool hasEbayRule = false;
                    foreach (var rule in settings.ActiveRules)
                    {
                        if (rule.TargetCategory == "Lens") hasLensRule = true;
                        if (rule.TargetCategory == "Mirrorless") hasMirrorlessRule = true;
                        if (rule.Scope == RuleScope.AllEbay) hasEbayRule = true;

                        // Upgrade old defaults
                        if (rule.RuleName == "Default Used Gear Rule" && rule.RuleValue == 0.15m && rule.CalculationType == PayoutType.PercentageOfPrice)
                        {
                            rule.RuleValue = 0.03m;
                            modified = true;
                        }
                        else if (rule.RuleName == "Default Warranty Rule" && rule.RuleValue == 25.00m && rule.CalculationType == PayoutType.FlatRate)
                        {
                            rule.CalculationType = PayoutType.PercentageOfPrice;
                            rule.RuleValue = 0.10m;
                            modified = true;
                        }
                    }

                    if (!hasLensRule)
                    {
                        settings.ActiveRules.Add(new CommissionRule
                        {
                            RuleName = "Lens Standard Rule",
                            PriorityOrder = settings.ActiveRules.Count,
                            Scope = RuleScope.CategorySpecific,
                            TargetCategory = "Lens",
                            CalculationType = PayoutType.PercentageOfPrice,
                            RuleValue = 0.08m
                        });
                        modified = true;
                    }
                    if (!hasMirrorlessRule)
                    {
                        settings.ActiveRules.Add(new CommissionRule
                        {
                            RuleName = "Mirrorless Standard Rule",
                            PriorityOrder = settings.ActiveRules.Count,
                            Scope = RuleScope.CategorySpecific,
                            TargetCategory = "Mirrorless",
                            CalculationType = PayoutType.PercentageOfPrice,
                            RuleValue = 0.05m
                        });
                        modified = true;
                    }
                    bool needsReprioritization = false;
                    if (hasEbayRule)
                    {
                        var ebayRule = settings.ActiveRules.FirstOrDefault(r => r.Scope == RuleScope.AllEbay);
                        if (ebayRule != null)
                        {
                            var firstCategorySpecific = settings.ActiveRules
                                .Where(r => r.Scope == RuleScope.CategorySpecific)
                                .OrderBy(r => r.PriorityOrder)
                                .FirstOrDefault();

                            if (firstCategorySpecific != null && firstCategorySpecific.PriorityOrder < ebayRule.PriorityOrder)
                            {
                                needsReprioritization = true;
                            }
                        }
                    }

                    if (!hasEbayRule || needsReprioritization)
                    {
                        var nonCategoryRules = new List<CommissionRule>();
                        var categoryRules = new List<CommissionRule>();

                        foreach (var rule in settings.ActiveRules)
                        {
                            if (rule.Scope == RuleScope.CategorySpecific)
                            {
                                categoryRules.Add(rule);
                            }
                            else if (rule.Scope != RuleScope.AllEbay)
                            {
                                nonCategoryRules.Add(rule);
                            }
                        }

                        var newEbayRule = settings.ActiveRules.FirstOrDefault(r => r.Scope == RuleScope.AllEbay) ?? new CommissionRule
                        {
                            RuleName = "Default eBay Rule",
                            Scope = RuleScope.AllEbay,
                            CalculationType = PayoutType.PercentageOfPrice,
                            RuleValue = 0.10m
                        };

                        nonCategoryRules.Add(newEbayRule);

                        // Order non-category rules by their existing priority, then append category rules
                        var orderedNonCategory = nonCategoryRules.OrderBy(r => r.PriorityOrder).ToList();
                        var orderedCategory = categoryRules.OrderBy(r => r.PriorityOrder).ToList();

                        var combined = new List<CommissionRule>();
                        combined.AddRange(orderedNonCategory);
                        combined.AddRange(orderedCategory);

                        for (int i = 0; i < combined.Count; i++)
                        {
                            combined[i].PriorityOrder = i;
                        }

                        settings.ActiveRules = combined;
                        modified = true;
                    }
                }

                if (modified)
                {
                    Settings.Update(settings);
                }
            }
        }

        private UserSettings CreateDefaultSettings()
        {
            return new UserSettings
            {
                Id = Guid.NewGuid(),
                UserDisplayName = "Sales Representative",
                ProductCategories = new List<AppCategory>
                {
                    new AppCategory { Name = "Lens", IsSystemPreset = true, IsActive = true },
                    new AppCategory { Name = "SLR", IsSystemPreset = true, IsActive = true },
                    new AppCategory { Name = "Digital - SLR", IsSystemPreset = true, IsActive = true },
                    new AppCategory { Name = "Mirrorless", IsSystemPreset = true, IsActive = true },
                    new AppCategory { Name = "TLR", IsSystemPreset = true, IsActive = true },
                    new AppCategory { Name = "Medium Format", IsSystemPreset = true, IsActive = true },
                    new AppCategory { Name = "Point and Shoot", IsSystemPreset = true, IsActive = true },
                    new AppCategory { Name = "Rangefinder", IsSystemPreset = true, IsActive = true },
                    new AppCategory { Name = "Camcorder", IsSystemPreset = true, IsActive = true },
                    new AppCategory { Name = "Bridge Camera", IsSystemPreset = true, IsActive = true },
                    new AppCategory { Name = "Light Meter/Flash", IsSystemPreset = true, IsActive = true },
                    new AppCategory { Name = "Tripod", IsSystemPreset = true, IsActive = true },
                    new AppCategory { Name = "Converter/Extender", IsSystemPreset = true, IsActive = true },
                    new AppCategory { Name = "Bag/Accessory", IsSystemPreset = true, IsActive = true }
                },
                WarrantyTypes = new List<AppWarrantyType>
                {
                    new AppWarrantyType { Name = "Sony", IsSystemPreset = true, IsActive = true },
                    new AppWarrantyType { Name = "Fuji", IsSystemPreset = true, IsActive = true },
                    new AppWarrantyType { Name = "Nikon", IsSystemPreset = true, IsActive = true },
                    new AppWarrantyType { Name = "Mack", IsSystemPreset = true, IsActive = true },
                    new AppWarrantyType { Name = "Canon", IsSystemPreset = true, IsActive = true }
                },
                ActiveRules = new List<CommissionRule>
                {
                    new CommissionRule
                    {
                        RuleName = "Default Used Gear Rule",
                        PriorityOrder = 0,
                        Scope = RuleScope.AllUsed,
                        CalculationType = PayoutType.PercentageOfPrice,
                        RuleValue = 0.03m // 3%
                    },
                    new CommissionRule
                    {
                        RuleName = "Default Warranty Rule",
                        PriorityOrder = 1,
                        Scope = RuleScope.AllWarranty,
                        CalculationType = PayoutType.PercentageOfPrice,
                        RuleValue = 0.10m // 10%
                    },
                    new CommissionRule
                    {
                        RuleName = "Default eBay Rule",
                        PriorityOrder = 2,
                        Scope = RuleScope.AllEbay,
                        CalculationType = PayoutType.PercentageOfPrice,
                        RuleValue = 0.10m // 10%
                    },
                    new CommissionRule
                    {
                        RuleName = "Lens Standard Rule",
                        PriorityOrder = 3,
                        Scope = RuleScope.CategorySpecific,
                        TargetCategory = "Lens",
                        CalculationType = PayoutType.PercentageOfPrice,
                        RuleValue = 0.08m // 8%
                    },
                    new CommissionRule
                    {
                        RuleName = "Mirrorless Standard Rule",
                        PriorityOrder = 4,
                        Scope = RuleScope.CategorySpecific,
                        TargetCategory = "Mirrorless",
                        CalculationType = PayoutType.PercentageOfPrice,
                        RuleValue = 0.05m // 5%
                    }
                }
            };
        }

        public void Dispose()
        {
            _db.Dispose();
        }
    }
}

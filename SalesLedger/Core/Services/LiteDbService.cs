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
            var dbPath = Path.Combine(appDataDir, "salesledger.db");

            // Setup polymorphic mapping for LiteDB
            var mapper = new BsonMapper();
            mapper.Entity<SaleRecord>().Id(x => x.Id);

            _db = new LiteDatabase(dbPath, mapper);
            InitializeDefaultSettings();

#if DEBUG
            SeedDebugData(force: false);
#endif
        }

        public LiteDbService(string customDbPath)
        {
            var directory = Path.GetDirectoryName(customDbPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var mapper = new BsonMapper();
            mapper.Entity<SaleRecord>().Id(x => x.Id);

            _db = new LiteDatabase(customDbPath, mapper);
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
                        if (rule is { RuleName: "Default Used Gear Rule", RuleValue: 0.15m, CalculationType: PayoutType.PercentageOfPrice })
                        {
                            rule.RuleValue = 0.03m;
                            modified = true;
                        }
                        else if (rule is { RuleName: "Default Warranty Rule" } and ({ RuleValue: 25.00m, CalculationType: PayoutType.FlatRate } or { RuleValue: 0.10m, CalculationType: PayoutType.PercentageOfPrice }))
                        {
                            rule.CalculationType = PayoutType.PercentageOfNetProfit;
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
                ProductCategories = [
                    new() { Name = "Lens", IsSystemPreset = true, IsActive = true },
                    new() { Name = "SLR", IsSystemPreset = true, IsActive = true },
                    new() { Name = "Digital - SLR", IsSystemPreset = true, IsActive = true },
                    new() { Name = "Mirrorless", IsSystemPreset = true, IsActive = true },
                    new() { Name = "TLR", IsSystemPreset = true, IsActive = true },
                    new() { Name = "Medium Format", IsSystemPreset = true, IsActive = true },
                    new() { Name = "Point and Shoot", IsSystemPreset = true, IsActive = true },
                    new() { Name = "Rangefinder", IsSystemPreset = true, IsActive = true },
                    new() { Name = "Camcorder", IsSystemPreset = true, IsActive = true },
                    new() { Name = "Bridge Camera", IsSystemPreset = true, IsActive = true },
                    new() { Name = "Light Meter/Flash", IsSystemPreset = true, IsActive = true },
                    new() { Name = "Tripod", IsSystemPreset = true, IsActive = true },
                    new() { Name = "Converter/Extender", IsSystemPreset = true, IsActive = true },
                    new() { Name = "Bag/Accessory", IsSystemPreset = true, IsActive = true }
                ],
                WarrantyTypes = [
                    new() { Name = "Sony", IsSystemPreset = true, IsActive = true },
                    new() { Name = "Fuji", IsSystemPreset = true, IsActive = true },
                    new() { Name = "Nikon", IsSystemPreset = true, IsActive = true },
                    new() { Name = "Mack", IsSystemPreset = true, IsActive = true },
                    new() { Name = "Canon", IsSystemPreset = true, IsActive = true }
                ],
                ActiveRules = [
                    new()
                    {
                        RuleName = "Default Used Gear Rule",
                        PriorityOrder = 0,
                        Scope = RuleScope.AllUsed,
                        CalculationType = PayoutType.PercentageOfPrice,
                        RuleValue = 0.03m // 3%
                    },
                    new()
                    {
                        RuleName = "Default Warranty Rule",
                        PriorityOrder = 1,
                        Scope = RuleScope.AllWarranty,
                        CalculationType = PayoutType.PercentageOfNetProfit,
                        RuleValue = 0.10m // 10%
                    },
                    new()
                    {
                        RuleName = "Default eBay Rule",
                        PriorityOrder = 2,
                        Scope = RuleScope.AllEbay,
                        CalculationType = PayoutType.PercentageOfPrice,
                        RuleValue = 0.10m // 10%
                    },
                    new()
                    {
                        RuleName = "Lens Standard Rule",
                        PriorityOrder = 3,
                        Scope = RuleScope.CategorySpecific,
                        TargetCategory = "Lens",
                        CalculationType = PayoutType.PercentageOfPrice,
                        RuleValue = 0.08m // 8%
                    },
                    new()
                    {
                        RuleName = "Mirrorless Standard Rule",
                        PriorityOrder = 4,
                        Scope = RuleScope.CategorySpecific,
                        TargetCategory = "Mirrorless",
                        CalculationType = PayoutType.PercentageOfPrice,
                        RuleValue = 0.05m // 5%
                    }
                ]
            };
        }

        public void Dispose()
        {
            _db.Dispose();
        }

#if DEBUG
        public void SeedDebugData(bool force = false)
        {
            if (!force && Sales.Exists(Query.All()))
            {
                return;
            }

            if (force)
            {
                Sales.DeleteAll();
            }

            var now = DateTime.Now;
            var settings = GetUserSettings();
            var rules = settings.ActiveRules ?? [];
            var commissionProc = new CommissionProcessor();

            var sampleSales = new List<SaleRecord>
            {
                new StandardSale
                {
                    InvoiceNumber = "INV-2026-001",
                    Sku = "SONY-A7IV",
                    ProductName = "Sony Alpha 7 IV Mirrorless Camera",
                    Category = "Mirrorless",
                    SalePrice = 2499.99m,
                    TransactionDate = now.AddDays(-15),
                    Status = PayoutStatus.Pending,
                    IsUsedGear = false
                },
                new StandardSale
                {
                    InvoiceNumber = "INV-2026-002",
                    Sku = "CANON-2470U",
                    ProductName = "Canon EF 24-70mm f/2.8L II USM (Used)",
                    Category = "Lens",
                    SalePrice = 1299.00m,
                    TransactionDate = now.AddDays(-12),
                    Status = PayoutStatus.Pending,
                    IsUsedGear = true
                },
                new WarrantySale
                {
                    InvoiceNumber = "INV-2026-003",
                    Sku = "WRY-SONY-3Y",
                    ProductName = "3-Year Camera Accidental Protection Plan",
                    Category = "Warranty",
                    SalePrice = 299.99m,
                    TransactionDate = now.AddDays(-10),
                    Status = PayoutStatus.Pending,
                    WarrantyTypeName = "Sony",
                    ManufacturerPrice = 120.00m
                },
                new EbaySale
                {
                    InvoiceNumber = "EBAY-99821",
                    Sku = "NIKON-D850-U",
                    ProductName = "Nikon D850 DSLR Camera Body",
                    Category = "Digital - SLR",
                    SalePrice = 1899.50m,
                    TransactionDate = now.AddDays(-8),
                    Status = PayoutStatus.Pending,
                    IsUsedGear = true
                },
                new StandardSale
                {
                    InvoiceNumber = "INV-2026-004",
                    Sku = "FUJI-XT5",
                    ProductName = "Fujifilm X-T5 Mirrorless Camera",
                    Category = "Mirrorless",
                    SalePrice = 1699.99m,
                    TransactionDate = now.AddDays(-5),
                    Status = PayoutStatus.Pending,
                    IsUsedGear = false
                },
                new StandardSale
                {
                    InvoiceNumber = "INV-2026-005",
                    Sku = "SONY-70200",
                    ProductName = "Sony FE 70-200mm f/2.8 GM OSS II",
                    Category = "Lens",
                    SalePrice = 2799.00m,
                    TransactionDate = now.AddDays(-3),
                    Status = PayoutStatus.Pending,
                    IsUsedGear = false
                }
            };

            foreach (var sale in sampleSales)
            {
                sale.CalculatedCommission = commissionProc.CalculateLineItem(sale, rules);
                Sales.Insert(sale);
            }
        }
#endif
    }
}

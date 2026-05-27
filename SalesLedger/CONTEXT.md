# TECHNICAL PRODUCT REQUIREMENT DOCUMENT (PRD)

**Project Title:** Standalone Sales Ledger, Commission Analytics, and Reporting Engine

**Target Deployment OS:** Windows 10 / 11 (AMD64 Architecture)

**Distribution Target:** Self-Contained Native Binary (`.exe`) with Background Self-Updater

---

## 1. Executive Summary & Architectural Goals

The purpose of this project is to build a high-performance local desktop tool to replace cloud-based spreadsheet templates. It tracks transaction logs, calculates complex commissions, provides analytical dashboards, handles returns, and manages document reporting.

The application must operate entirely on the client host machine without background service installations, docker dependencies, or cloud network requirements.

### Core Objectives:

* **Zero-Lag UI Canvas:** Provide a data-dense, spreadsheet-like grid rendering thousands of sales rows with hardware-accelerated, high frame-rate scrolling.
* **Dual-Storage Mechanics:** Combine a safe, document-based transactional storage layer with a separate, vectorized database for split-second multi-year query calculations.
* **Dynamic Commission Engine:** Evaluate multi-tier commission structures natively, seamlessly resolving overlapping conditional rules without calculation drift.
* **Immutable Ledger Adjustments:** Implement a robust double-entry style accounting model for product returns, protecting historical audit trails by using negative current-period offsets for post-payout returns.
* **Automated Release Pipeline:** Bundle an embedded code delivery network that handles silent version pushes, dependency management, and background execution swaps.

---

## 2. Developer Tooling & Core Runtime Environment

To compile, test, and package this system cleanly with zero environmental friction, developers must use the following configuration baselines:

* **Primary IDE:** **JetBrains Rider** (Optimized for XAML/axaml cross-platform designer tools and deep .NET profiling integrations).
* **Development Framework & SDK:** **.NET 10.0 LTS** (Using C# 14 syntax constraints).
* **Target Configuration Compilation:** Native Ahead-of-Time compilation (`<PublishAot>true</PublishAot>`). The codebase must exclude dynamic reflection paradigms that block trimming optimization, ensuring it builds into an unmanaged, native machine code executable file.

---

## 3. High-Performance Hybrid Tech Stack

```
+-----------------------------------------------------------------+
|                       AVALONIA UI CANVAS                        |
|   (Skia Engine Rendering, Hardware Accelerated, Virtual Grid)   |
+-----------------------------------------------------------------+
                                |
                   Data Binding & Event Triggers
                                |
+-----------------------------------------------------------------+
|               VIEWMODEL LAYER (CommunityToolkit)                |
|       (Asynchronous Task Handling, UI Thread Isolation)        |
+-----------------------------------------------------------------+
                                |
             Service Domain Boundary (C# Interfaces)
                                |
        +-----------------------+-----------------------+
        | (Transactional OLTP)                          | (Analytical OLAP)
        v                                               v
+-------------------------------+               +-------------------------------+
|    LITEDB ENGINE (.db)        |               |    DUCKDB ENGINE (.duckdb)    |
|-------------------------------|               |-------------------------------|
| Pure C# Document Store        |               | Embedded Columnar Storage     |
| Real-Time Row Mutations       |               | Vectorized Analytical Querying|
| Schema-less Operational Store |               | Aggregated Report Engine      |
+-------------------------------+               +-------------------------------+

```

### 3.1 UI & Layout Engine: Avalonia UI (v12+)

Bypasses standard OS layout controls and DOM web renderers. Utilizes the **Skia Graphics Library** to draw UI layouts natively on the screen via hardware acceleration. It implements deep layout virtualization for tables, creating and recycling visual UI controls only for rows actively displaying inside the viewer frame.

### 3.2 State Management: CommunityToolkit.Mvvm

Implements compile-time source generators to build type-safe parameters, handling observable fields and cross-thread event orchestration. This keeps calculations off the interface threads, guaranteeing zero interface stuttering.

### 3.3 Transactional Storage (OLTP): LiteDB

An embedded, serverless document ledger written natively in C#. It maps app objects directly to binary JSON (BSON) files. This handles single-line transactions, updates, user edits, and runtime setting updates.

### 3.4 Analytical Analytics Engine (OLAP): DuckDB

An embedded, columnar data workspace wrapped by the `DuckDB.NET.Data` provider. It organizes and reads datasets vertically by column, aggregating thousands of historic records over custom dates within milliseconds.

### 3.5 Document Generation Subsystem: QuestPDF

A programmatic component assembly library written in C#. It formats vector graphics, data matrix summaries, and structural layouts directly into text-sharp, print-ready PDF summaries without opening external browser windows.

---

## 4. Unified Domain Architecture & Polymorphic Schemas

Data models are represented natively as objects inside the codebase, using explicit polymorphism to track distinct transaction variations under unified storage wrappers.

### 4.1 Transaction Domains & Schema Blueprint

The system tracks three distinct categories of sales records: **Standard Equipment Sales**, **Warranty Coverage Sales**, and **Return Offset Sales**. They share structural properties but handle ledger balances differently based on the pay cycle state.

```csharp
namespace SalesLedger.Core.Models
{
    public enum SaleType { Standard, Warranty, ReturnOffset }
    public enum PayoutStatus { Pending, Paid, ReturnedBeforePayout }
    public enum PayoutType { PercentageOfPrice, FlatRate, PercentageOfNetProfit }
    public enum RuleScope { AllUsed, AllWarranty, CategorySpecific }

    public abstract class SaleRecord 
    {
        public Guid Id { get; set; }
        public SaleType RecordType { get; protected set; }
        public PayoutStatus Status { get; set; } = PayoutStatus.Pending;
        public Guid? AssociatedReportId { get; set; } // Identifies the report that locked this record
        public DateTime TransactionDate { get; set; }
        public string InvoiceNumber { get; set; }
        public string Sku { get; set; }
        public string ProductName { get; set; }
        public string Category { get; set; } // Matches AppCategory constraints
        public decimal SalePrice { get; set; }
        public decimal CalculatedCommission { get; set; }
    }

    public class StandardSale : SaleRecord 
    {
        public bool IsUsedGear { get; set; }
        public StandardSale() => RecordType = SaleType.Standard;
    }

    public class WarrantySale : SaleRecord 
    {
        public string WarrantyTypeName { get; set; } // Matches AppWarrantyType constraints
        public decimal ManufacturerPrice { get; set; } // The wholesale cost to the business
        public decimal NetMargin => SalePrice - ManufacturerPrice;
        public WarrantySale() => RecordType = SaleType.Warranty;
    }

    public class ReturnOffsetSale : SaleRecord
    {
        public Guid OriginalSaleId { get; set; }
        public ReturnOffsetSale() => RecordType = SaleType.ReturnOffset;
    }

    public class PayoutReport
    {
        public Guid Id { get; set; }
        public DateTime ReportGeneratedTimestamp { get; set; }
        public string ReportName { get; set; } // e.g., "June 2026 Submission"
        public decimal TotalCommissionCalculated { get; set; }
        public List<Guid> LockedSaleIds { get; set; } = new();
    }
}

```

### 4.2 Configuration & Metadata Schema

```csharp
namespace SalesLedger.Core.Models
{
    public class UserSettings 
    {
        public Guid Id { get; set; }
        public string UserDisplayName { get; set; }
        public List<AppCategory> ProductCategories { get; set; } = new();
        public List<AppWarrantyType> WarrantyTypes { get; set; } = new();
        public List<CommissionRule> ActiveRules { get; set; } = new();
    }

    public class AppCategory
    {
        public string Name { get; set; }
        public bool IsSystemPreset { get; set; } // If true, UI blocks modification/removal
        public bool IsActive { get; set; } // Handles Soft-Delete states
    }

    public class AppWarrantyType
    {
        public string Name { get; set; }
        public bool IsSystemPreset { get; set; }
        public bool IsActive { get; set; }
    }
}

```

### 4.3 Data Integrity & Soft-Delete Enforcement

To protect historical records from data corruption when a user deletes a custom category or warranty type from the settings page, the app implements two safety validation patterns:

1. **The Soft-Delete Pattern:** Deletion commands do not purge metadata entries from the database configuration rows. Instead, the application toggles `IsActive = false`. Inactive custom types are hidden from new data-entry dropdown selection components, but remain preserved inside the database engine so old logs still render their historical data cleanly.
2. **Referential Block Checks:** If an absolute deletion is requested, the service layer queries the master table ledger before approving the command:

```csharp
bool isUsed = liteDb.GetCollection<SaleRecord>("sales")
                    .Exists(x => x.Category == targetCategoryName);

```

If true, the application blocks the deletion, alerts the user, and shifts the operation into a soft-deactivation state instead.

---

## 5. Commission Engine & Overlap Resolution

### 5.1 The Priority Waterfall Pattern

When a transaction satisfies multiple commission conditions (e.g., a line item is marked as *Used Gear*, but its assigned category *Lenses* carries a separate dedicated rule), the engine applies a **User-Ranked Priority Waterfall (First-Match Wins)** to solve collision loops.

Rules carry a tracking property named `PriorityOrder`. The application processes transactions from top to bottom through this array; **the very first rule to return a valid match applies its math and exits further evaluation immediately.**

```csharp
namespace SalesLedger.Core.Models
{
    public class CommissionRule
    {
        public string RuleId { get; set; }
        public string RuleName { get; set; }
        public int PriorityOrder { get; set; } // Execution ranking index (0 = Top Precedence)
        public RuleScope Scope { get; set; }
        public string TargetCategory { get; set; } // Used only if Scope == CategorySpecific
        public PayoutType CalculationType { get; set; }
        public decimal RuleValue { get; set; } // Stores decimal rates (e.g., 0.12) or flat-rates (e.g., 25.00)
    }
}

```

### 5.2 Core Calculation Service Implementation

```csharp
namespace SalesLedger.Core.Services
{
    public class CommissionProcessor
    {
        public decimal CalculateLineItem(SaleRecord sale, List<CommissionRule> activeRules)
        {
            // Enforce sorting sequence matching the defined waterfall ranking hierarchy
            var waterfall = activeRules.OrderBy(r => r.PriorityOrder).ToList();

            foreach (var rule in waterfall)
            {
                bool isMatch = false;

                switch (rule.Scope)
                {
                    case RuleScope.AllWarranty:
                        isMatch = (sale is WarrantySale);
                        break;
                    case RuleScope.AllUsed:
                        isMatch = (sale is StandardSale standardItem && standardItem.IsUsedGear);
                        break;
                    case RuleScope.CategorySpecific:
                        isMatch = string.Equals(sale.Category, rule.TargetCategory, StringComparison.OrdinalIgnoreCase);
                        break;
                }

                if (isMatch)
                {
                    return rule.CalculationType == PayoutType.PercentageOfPrice
                        ? Math.Round(sale.SalePrice * rule.RuleValue, 2)
                        : rule.RuleValue;
                }
            }

            return 0.00m; // Fallback value if zero conditional rules evaluate to true
        }
    }
}

```

### 5.3 Financial Ledger Adjustments & Period Closing

This architectural service handles the processing logic for state transitions when performing product returns or freezing an active pay cycle:

```csharp
namespace SalesLedger.Core.Services
{
    public class PayoutLedgerService
    {
        private readonly LiteDB.ILiteRepository _db;

        public PayoutLedgerService(LiteDB.ILiteRepository db) => _db = db;

        public void ProcessReturn(SaleRecord originalSale)
        {
            if (originalSale.Status == PayoutStatus.Pending)
            {
                // Branch A: Pre-payout modification
                originalSale.Status = PayoutStatus.ReturnedBeforePayout;
                originalSale.CalculatedCommission = 0.00m;
                _db.Update(originalSale);
            }
            else if (originalSale.Status == PayoutStatus.Paid)
            {
                // Branch B: Post-payout legacy balance ledger offset injection
                var offset = new ReturnOffsetSale
                {
                    Id = Guid.NewGuid(),
                    OriginalSaleId = originalSale.Id,
                    TransactionDate = DateTime.UtcNow, // Belongs to current open calculation pool
                    InvoiceNumber = originalSale.InvoiceNumber,
                    Sku = originalSale.Sku,
                    ProductName = $"[RETURN OFFSET] - {originalSale.ProductName}",
                    Category = originalSale.Category,
                    SalePrice = -originalSale.SalePrice, // Balanced accounting negative entry
                    CalculatedCommission = -originalSale.CalculatedCommission, // Balances total upcoming payout
                    Status = PayoutStatus.Pending
                };
                _db.Insert<SaleRecord>(offset);
            }
        }

        public PayoutReport CloseCurrentPayPeriod(string reportName)
        {
            var pendingSales = _db.Query<SaleRecord>()
                                  .Where(x => x.Status == PayoutStatus.Pending)
                                  .ToList();

            var report = new PayoutReport
            {
                Id = Guid.NewGuid(),
                ReportGeneratedTimestamp = DateTime.UtcNow,
                ReportName = reportName,
                TotalCommissionCalculated = pendingSales.Sum(s => s.CalculatedCommission),
                LockedSaleIds = pendingSales.Select(s => s.Id).ToList()
            };

            // Transition open items permanently into locked history logs
            foreach (var sale in pendingSales)
            {
                sale.Status = PayoutStatus.Paid;
                sale.AssociatedReportId = report.Id;
                _db.Update(sale);
            }

            _db.Insert(report);
            return report;
        }
    }
}

```

---

## 6. User Interface Workspace & Application Navigation

The application uses a **Single-Window View-Model Swapping Pattern**, avoiding messy multi-window states. Visual components load within a core main container panel handled via standard data binding hooks.

```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2000/xaml"
        xmlns:vm="using:SalesLedger.Core.ViewModels"
        xmlns:views="using:SalesLedger.Core.Views"
        x:Class="SalesLedger.Core.Views.MainWindow"
        Title="Sales Ledger &amp; Analytics Canvas" Width="1200" Height="750">
    
    <Grid RowDefinitions="Auto, *">
        <Border Grid.Row="0" Background="#1e272c" Height="55">
            <Grid ColumnDefinitions="*, Auto">
                <TextBlock Text="Local Performance Analytics Canvas" VerticalAlignment="Center" Margin="20,0" Foreground="#ecf0f1" FontSize="16" FontWeight="SemiBold"/>
                <Button Grid.Column="1" Content="Settings Panel" Command="{Binding ToggleWorkspaceCommand}" Margin="15,0" Padding="12,6"/>
            </Grid>
        </Border>

        <TransitioningContentControl Grid.Row="1" Content="{Binding CurrentActiveWorkspace}">
            <TransitioningContentControl.DataTemplates>
                <DataTemplate DataType="vm:LedgerDashboardViewModel">
                    <views:LedgerDashboardView />
                </DataTemplate>
                <DataTemplate DataType="vm:FullScreenSettingsViewModel">
                    <views:FullScreenSettingsView />
                </DataTemplate>
            </TransitioningContentControl.DataTemplates>
        </TransitioningContentControl>
    </Grid>
</Window>

```

### 6.1 Context-Sensitive Ledger Actions & Closing Controls

The ledger interface and dashboard views incorporate specialized layout controls to support modifications, deletions, and cycle execution safety:

* **The Main Grid Context Actions Pane:** Selecting any row inside the Virtualized DataGrid populates an contextual action layout containing **"Edit Entry"** and **"Process Return"** buttons. If the record's `Status` field reads `ReturnedBeforePayout`, the action controls instantly grey out. Clicking "Process Return" prompts the backend service layer to execute the target conditional branch cleanly.
* **The Payout Control Center:** A prominent visual component positioned on the active dashboard workspace showing the current month's calculated rolling commission balance. It includes a validation execution button: **"Finalize Period & Export Report"**. Triggering this command calls a validation modal view, freezes open rows to status `Paid`, saves a new `PayoutReport` tracking document, and builds a print-ready file using the QuestPDF engine.

### 6.2 The Full-Screen Settings Layout Tabs

When toggled into the Settings workspace view, the layout opens full-screen, showing options cleanly divided across three functional tabs:

1. **Identity Profile Tab:** Input form fields to define the user's name (`UserDisplayName`), manage file pathways, and display basic system diagnostics.
2. **Taxonomy & Lists Tab:** Live grid split into twin management panes tracking product categories and warranty types. Pre-existing system entries are visually locked down with their text labels greyed out. Custom items display an action button to toggle `IsActive`, keeping validation workflows simple and error-free.
3. **Commission Rules Waterfall Tab:** A vertical interface displaying active commission processing rules. It features drag handles or simple up/down action buttons, letting users quickly rearrange the sequence list to change how rule priorities evaluate.

---

## 7. CI/CD Pipeline & Automated Updates

To deploy new feature releases smoothly across other user workstations without manual packaging adjustments, the software utilizes **Velopack** paired with automated remote distribution triggers.

```
+---------------------+      +------------------------+      +-----------------------+
|  Push Code to Main  | ---> | GitHub Actions Windows | ---> | Compiles Native AOT & |
|  (GitHub Repository) |      | Virtual Build Runner   |      | Runs Velopack Pack    |
+---------------------+      +------------------------+      +-----------------------+
                                                                         |
                                                                         v
+---------------------+      +------------------------+      +-----------------------+
| App Patches Implemented| <--- | Silent Client Payload  | <--- | Releases Uploaded to  |
| on Next Launch      |      | Background Download    |      | Static Remote Storage |
+---------------------+      +------------------------+      +-----------------------+

```

### 7.1 Build Server Compilation Matrix (`.github/workflows/release.yml`)

When updates are pushed to the core repository, a dedicated build script automates compilation tasks on a virtual Windows runner:

1. Restores native NuGet asset dependencies.
2. Triggers compilation targeted straight for production architectures:
   `dotnet publish -r win-x64 -c Release /p:PublishAot=true -o ./publish`
3. Invokes the Velopack command-line compiler suite to build delta update packages and standard installer variants:
   `vpk pack -u SalesLedgerApp -v ${{ github.ref_name }} -p ./publish -e LedgerUI.exe`
4. Pushes compiled update payloads directly onto a public or private cloud storage folder (e.g., GitHub Releases, AWS S3, or a standard web host).

### 7.2 Embedded Client Auto-Updater Routine

When the app launches, a background worker uses Velopack's native SDK to silently check for software updates:

```csharp
public async Task CheckForApplicationUpdatesAsync()
{
    try
    {
        var updateManager = new Velopack.UpdateManager("https://your-release-storage-endpoint.com/vpk-releases");
        var updateInfo = await updateManager.CheckForUpdatesAsync();
        
        if (updateInfo != null)
        {
            // Download update differences in the background without locking the UI thread
            await updateManager.DownloadUpdatesAsync(updateInfo);
            
            // Apply updates smoothly during the next application launch lifecycle
            updateManager.ApplyUpdatesAndExit();
        }
    }
    catch (Exception)
    {
        // Fail silently to safeguard application operations if the host machine goes offline
    }
}

```

---

## 8. Phased Implementation Roadmap & Engineering Tasks

### Phase 1: Data Layers & Cross-Storage Synchronization

* **Task 1.1:** Instantiate the basic .NET solution architecture inside JetBrains Rider. Turn on explicit compilation rules enforcing native ahead-of-time code layout properties inside the core configuration file (`.csproj`).
* **Task 1.2:** Install native package versions via NuGet: `LiteDB`, `DuckDB.NET.Data`, `CommunityToolkit.Mvvm`, and `Velopack`.
* **Task 1.3:** Build the C# data definitions for sales collections (`StandardSale`, `WarrantySale`, `ReturnOffsetSale`), configuration structures, report trackers, and commission parameters.
* **Task 1.4:** Build the synchronization worker pipeline. Set up an internal queue channel (`System.Threading.Channels`) that captures real-time data adjustments from LiteDB and mirrors flattened relational layouts down to the columnar DuckDB file structure in the background.

### Phase 2: Commission Resolution & Core Document Assembly

* **Task 2.1:** Implement the `CommissionProcessor` logic. Write unit tests evaluating complex edge cases to verify that rule overlaps resolve correctly matching the user-defined precedence settings.
* **Task 2.2:** Build the database transaction routing features inside the `PayoutLedgerService` wrapper to safely handle pre-payout modifications and negative adjustment creations.
* **Task 2.3:** Set up the DuckDB aggregation layer. Write optimized analytical views that calculate sales numbers, group items by categories, and summarize margins over custom date inputs.
* **Task 2.4:** Build the document reporting pipeline using QuestPDF. Code modular component structures to construct tables, metrics cards, and document footers, feeding output streams directly into vector PDF documents.

### Phase 3: Presentation UI & Navigation Workspaces

* **Task 3.1:** Build the core shell window frame layout (`MainWindow.axaml`). Implement the `TransitioningContentControl` module and wire it to active workspace properties using standard MVVM data bindings.
* **Task 3.2:** Build out the transactional ledger dashboard view. Set up the virtualized data grid component, connecting its row bindings to background-filtered arrays (`ObservableCollection`) to handle high row density with ease.
* **Task 3.3:** Build grid context control features (right-click popups or button menus) directly onto the DataGrid interface to let users view, update, or process returns on rows effortlessly.
* **Task 3.4:** Build out the input form views for new transactions, mapping distinct dropdown selection elements for standard product categories and warranty variations.
* **Task 3.5:** Build out the full-screen tabbed Settings view interface. Wire up the category lists to handle soft-deactivation toggles, and map the rule array to simple up/down sorting controls to manage priority configurations.

### Phase 4: CI/CD Packaging & Production Checks

* **Task 4.1:** Profile memory consumption baselines during long scrolling actions inside the main grid view to confirm that row virtualization is working correctly and memory footprint remains stable.
* **Task 4.2:** Run performance testing sweeps on concurrent write and query routines to confirm that the separate storage layers handle data operations smoothly without file-locking issues.
* **Task 4.3:** Script the GitHub Actions automated release file (`release.yml`). Run production compilation tests using the Velopack toolset to generate a unified Windows installer package (`Setup.exe`). Verify that downstream client app instances check, download, and apply updates successfully.
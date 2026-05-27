# Standalone Sales Ledger & Analytics Canvas

The **Standalone Sales Ledger** is a native Windows desktop application designed to track sales transactions, process multi-tiered commission waterfalls, manage double-entry return adjustments, and run sub-second OLAP analytical queries.

Built on **Avalonia UI** for the presentation layer, the application is optimized for **Native Ahead-of-Time (AOT)** compilation, delivering a self-contained, high-performance binary without virtual machine overhead.

---

## 1. System Overview & Architecture

To achieve sub-second analytical dashboard responsiveness while maintaining absolute write durability, the system employs a **CQRS-inspired Dual-Database Architecture**:

```
                  +--------------------------------+
                  |      Avalonia UI Desktop       |
                  +--------------------------------+
                     /                          \
             (Reads / Writes)            (OLAP Analytics)
                   v                              v
        +---------------------+        +--------------------+
        |       LiteDB        |        |      DuckDB        |
        |  (Operational DB)   |        |  (Analytical DB)   |
        +---------------------+        +--------------------+
                   |                              ^
          (Inserts / Updates)                     |
                   \                              /
                    +--> Background Sync Channel -+
```

### Key Architectural Pillars
* **Operational Store (LiteDB)**: A serverless, document-oriented NoSQL database that handles all transactional modifications (transaction additions, edits, deletes, return offsets, settings changes).
* **Analytical Store (DuckDB)**: An in-process columnar database that serves as our high-performance OLAP query engine, aggregating transactional metrics (ASP, Sales Volumes, Trend buckets) in sub-seconds.
* **Background Sync Pipeline**: Real-time write replication from LiteDB to DuckDB is offloaded to a background thread using a thread-safe `System.Threading.Channels` queue. This guarantees that operational writes never block the UI thread.
* **Trim-Safe Native AOT Compliance**: Built entirely without dynamic reflection, runtime type emitters, or assembly scan loops, allowing the code to be compiled directly into unmanaged native machine code (`win-x64`).

---

## 2. Walkthrough of Functionality

### 2.1 Transaction Management (Ledger Dashboard)
* **Standard & Warranty Transactions**: Users can record hardware sales (Standard) or protection plans (Warranty). Standard sales contain hardware-specific metadata (`IsUsedGear`), while warranty sales track plan categories (`Sony`, `Fuji`, `Nikon`, etc.) and manufacturer wholesale costs.
* **Hover Row Actions**: Action buttons (✏️ Edit, ↩️ Return, 🗑️ Delete) fade in contextually when hovering over any row in the ledger grid, removing visual clutter from the dashboard.
* **Interactive Filtering & Column Sorting**:
  * Users can search by invoices or products and filter by custom start/end dates.
  * A "High-Value" toggle filters the grid to premium products (`SalePrice >= $1,500`).
  * Clicking on headers sorts columns with toggleable reverse sorting.

### 2.2 Analytics Canvas (Sales Trends)
* **Sub-second OLAP Aggregations**: The dashboard aggregates volumes dynamically over customized intervals (Day, Week, Month) using DuckDB SQL.
* **Responsive Trend Columns**: Chart items dynamically resize to utilize the full width of the screen. Hovering over a column displays a detailed tooltip showing:
  * Total Sales Volume (Revenue)
  * Total Units Sold
  * Total Commissions Earned
  * Highest Value Single Sale
  * Product Category Distribution

### 2.3 Commission Waterfall Engine
Commissions are calculated instantly during entry or import through a user-prioritized waterfall of matching criteria:
* **All Used Gear Rule**: Triggers matching rules for physical equipment marked as *Used Gear* (Defaults to **3%** of Sale Price).
* **All Warranty Rule**: Triggers matching rules for protection packages (Defaults to **10%** of Sale Price).
* **Category Specific Rules**: Rules tied directly to product lines (e.g., Lenses at **8%**, Mirrorless at **5%**).
* **Payout Calculation Types**: Supports flat rates (e.g. `$25.00`), percentage of price, and percentage of net profit (warranties only: `(SalePrice - ManufacturerPrice) * Rate`).

### 2.4 CSV Import Wizard
Imports sales records from spreadsheets/spiff sheets:
* **Header Mapping**: Standardized matching rules map columns automatically.
* **Optional Fields & `[None]` Option**: If a field is not present in the CSV (e.g. wholesale price or used gear flags), users can select `[None]` to skip it and apply system-wide defaults (such as setting all imported items as used gear).
* **Context-Aware Import**: Importing as "Warranty" automatically hides the standard product category mapping and applies a default brand classification.

### 2.5 Period Finalization & Auto-Launch Report
* **Double-Entry Returns Processing**:
  * Returns on *pending* records wipe out the commission and mark the status as `ReturnedBeforePayout`.
  * Returns on *paid* records (locked periods) insert a negative `ReturnOffsetSale` into the active period, keeping historical sheets locked while adjusting active paychecks.
* **QuestPDF Commission Statements**: Closing a pay period generates a printable PDF report in the user's `Downloads` folder, automatically launching it in the system's default PDF viewer.

### 2.6 Full-Screen Settings
* **Taxonomy Presets**: Configure system-level product categories and warranty brands. Presets are soft-deletable to keep historical transactions intact while pruning future selection choices.
* **Interactive Rules Reordering**: Drag-and-drop rule rows to reprioritize their execution order within the waterfall engine.
* **Database Reset Tool**: Provides a developer utility under Settings -> Profile to instantly wipe the LiteDB and rebuild the DuckDB analytical tables.

---

## 3. Directory & Project Structure

The project code is modularized as follows:

```
SalesLedger/
│
├── Core/
│   ├── Models/
│   │   └── Models.cs                  # Base SaleRecord, StandardSale, WarrantySale, ReturnOffsetSale
│   │
│   ├── Services/
│   │   ├── LiteDbService.cs           # Document-oriented NoSQL operational database service
│   │   ├── DuckDbService.cs           # columnar OLAP analytical database service
│   │   ├── SyncPipeline.cs            # Channels-based background producer-consumer sync queue
│   │   ├── CommissionProcessor.cs     # Waterfall commission calculation logic
│   │   ├── PayoutLedgerService.cs     # Closing pay periods & double-entry return rules
│   │   ├── AnalyticsService.cs        # duckdb OLAP aggregations for dashboard trends
│   │   └── ReportGenerator.cs         # PDF reporting using QuestPDF layout definitions
│   │
│   ├── ViewModels/
│   │   ├── MainWindowViewModel.cs     # Swaps view states & checks update loops
│   │   ├── LedgerDashboardViewModel.cs# Manages transactions grid, chart data, and imports
│   │   └── FullScreenSettingsViewModel.cs # Manages presets, rule reordering, and DB resets
│   │
│   └── Views/
│       ├── MainWindow.axaml / .cs     # Main app frame
│       ├── LedgerDashboardView.axaml  # Grid view, hover action buttons, and chart tooltips
│       └── FullScreenSettingsView.axaml# Tabs for Profile, custom presets, and drag-and-drop rules
│
├── App.axaml / .cs                    # Avalonia App configuration & debugger hooks
├── Program.cs                         # Velopack bootstrapper & Application Entry Point
└── SalesLedger.csproj                 # MSBuild configurations including Native AOT compilation
```

---

## 4. Development & Build Commands

Ensure you have the **.NET 10 SDK** installed on Windows.

### Restore Dependencies
```powershell
dotnet restore
```

### Run Project Locally (Debug)
```powershell
dotnet run --project SalesLedger
```

### Run Unit Test Suite
Runs all unit and integration tests covering database replication, waterfall logic, returns, and CSV parsing:
```powershell
dotnet test
```

---

## 5. Releasing New Builds (Velopack & AOT)

To deploy new releases to production workstations, the application uses **Native AOT compilation** compiled into a **Velopack Installer**.

### Step 5.1: Compile Standalone Native Release
Publish the project in Release configuration targeting Windows x64 with Ahead-of-Time optimizations:
```powershell
dotnet publish SalesLedger\SalesLedger.csproj -r win-x64 -c Release /p:PublishAot=true -o ./publish
```
*Output will be generated under `./publish` (including `SalesLedger.exe` and native library bindings like `duckdb.dll` and `libSkiaSharp.dll`).*

### Step 5.2: Package with Velopack CLI
Pack the publish folder into a unified Windows Setup installer (`Setup.exe`) and delta-update files using Velopack CLI (`vpk` tool):

1. **Install/Update Velopack CLI tool globally**:
   ```powershell
   dotnet tool install -g vpk
   ```
2. **Compile the release installer**:
   ```powershell
   vpk pack -u SalesLedgerApp -v 1.0.0 -p ./publish -e SalesLedger.exe
   ```
   *Replace `1.0.0` with the target release version string.*

3. **Distribute Updates**:
   Copy the contents of the generated `Releases` folder (containing `Setup.exe`, `.nupkg` assets, and `RELEASES` metadata) to your update storage endpoint (e.g. GitHub Releases, AWS S3, or web host), matching the endpoint URL specified in:
   * [MainWindowViewModel.cs](file:///C:/Users/camer/RiderProjects/SalesLedger/SalesLedger/Core/ViewModels/MainWindowViewModel.cs#L73)

### Step 5.3: Automated CI/CD (GitHub Actions)
For a fully automated deployment pipeline, use the configured workflow in `.github/workflows/release.yml`:

1. **Commit and push** all changes to your repository.
2. **Tag your release** (e.g., `v0.0.1` for the first beta release):
   ```bash
   git tag v0.0.1
   git push origin v0.0.1
   ```
3. GitHub Actions will trigger automatically to compile the Native AOT build, package it with Velopack (generating setup and differential delta packages), create a new GitHub Release, and upload all assets.
4. Your colleagues can install the app by downloading the `Setup.exe` from that release. The application will check for and apply updates automatically on startup.


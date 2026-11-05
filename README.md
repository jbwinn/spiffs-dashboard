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
* **Standard, Warranty, & eBay Transactions**: Users can record hardware sales (Standard), protection plans (Warranty), or eBay listings (eBay). Standard and eBay sales contain hardware-specific metadata (`IsUsedGear`), while warranty sales track plan categories (`Sony`, `Fuji`, `Nikon`, etc.) and manufacturer wholesale costs. eBay sales dynamically rename the "Receipt" field to "Order Code".
* **Multi-Item Receipts**: A receipt is every sale that shares an invoice number (there is no separate receipt record; lookups ignore upper/lower case). **✎** on any sale row, paid ones included, opens the whole receipt. The clicked product opens in the fields for editing (if it's still pending), the rest of the receipt is listed below, and **✓ Done Editing, Add Another** / **+ Add Product to Receipt** adds more products:
  * **Pending** items can be edited (✎ on the line), removed, or returned.
  * **Paid** items, items returned before payout, and return lines are shown **read-only** (🔒) for context. Paid items can still be returned, which goes through the normal return process (a return line in the current period); they are never edited directly.
  * Returns chosen in the dialog are applied when the receipt is saved, and can be undone before then.
  * The invoice number is locked once part of the receipt is paid or returned, so the receipt can't be split.
* **Typing an Existing Invoice Number**: When adding a new transaction, leaving the invoice field with a number that already exists pulls that receipt into the dialog (the title changes to "Add to Existing Receipt …" and a notice shows its date and items); new products are added to it. Changing the number again drops those items and checks the new number. If you click Save before the lookup happened, the dialog shows the existing items first and asks you to save again.
* **Dates for Products Added to an Existing Receipt**: If the receipt's date isn't today, adding a product asks whether to use the **receipt's date** (the usual case) or **today's date**. The choice applies to further products added before saving. Lines dated differently from the receipt show "Dated …".
* **Existing Items Never Move Silently**: If you change the date field on a receipt that already has saved items, saving asks whether to **keep their original date** or **move them** to the new date.
* **Duplicate SKU Check**: Entering a **used** item or any **eBay** item checks its SKU against every earlier sale (new or used, any invoice, paid or not) and against other lines on the receipt. The check runs as soon as you leave the SKU field, and again when the product is added if it wasn't confirmed yet. It never blocks:
  * If every earlier sale of that SKU was **returned** (before payout, or fully refunded after payout), a **"Returned Item Being Sold Again"** notice shows the earlier sale and its return, and asks you to confirm the resale.
  * Otherwise (including partial refunds, where the customer kept the item), a **"Duplicate SKU"** warning shows the other sale(s) and asks whether to add it anyway.
  * New-gear and warranty entries aren't checked (they share SKUs legitimately); blank SKUs and an existing item whose SKU wasn't changed aren't checked either.
* **Number Fields**: Clicking a price or amount field selects its contents, so typing replaces the value.
* **Product Name Autofill**: The product name field suggests names already in the ledger (most-used first, gear and warranty plans suggested separately). Picking one fills in the category (or warranty brand) it was last sold under. There's no separate list to maintain: new names appear automatically once saved.
* **Hover Row Actions**: Action buttons (✎ Open Receipt / Edit Return, ↶ Return, ↺ Restore, ✕ Delete) fade in when hovering over a row in the ledger grid. Paid records can't be edited or deleted. Deleting a sale also deletes its return lines.
* **Grid Formatting, Resizing, & Sorting**:
  * Grid rows default to auto-fitting content so that text is never cut off.
  * Columns can be dragged to resize.
  * Clicking on column headers sorts the grid (dates descending by default, text alphabetically, or numeric fields by value).
  * Users can search by invoices/receipts or products and filter by sale type, category, and date range (including **Current Pay Period**: everything the next Finalize would include).
  * The **Returns** sale-type filter shows return lines, sales returned before payout, and legacy return-offset records.
  * A "High-Value" toggle filters the grid to premium products (`SalePrice >= $1,500`).

### 2.2 Analytics Canvas (Sales Trends)
* **Timeframes**: Presets (Current Month, Last Month, Last 30 Days, Last 3/6 Months, Year to Date), plus:
  * **Current Pay Period**: every unpaid sale and return, plus items returned before payout that aren't on a report yet, whatever their dates. It's exactly what the next Finalize would include, which doesn't always match the calendar month.
  * **Custom**: pick a start and end date (whole days, inclusive).
  * The KPI cards, trend chart and category distribution all follow the selected timeframe. The chart groups by day (up to ~6 weeks), week (up to ~6 months) or month, and a caption shows the dates covered.
* **KPI Cards**:
  * **Total Sales (Gross)**: everything sold in the range, including items later returned. Underneath: returns and **net sales** (gross − returns). Hovering shows the full breakdown with item and return counts.
  * **Total Commission Earned**: net of return clawbacks, i.e. what will actually be paid.
  * **Units Sold (Net)**: items sold minus items that came back. An item counts as returned when it's returned before payout or when its refunds reach the full price (each return line records whether it `CompletesRefund`). A partial refund where the customer keeps the item doesn't reduce units. **ASP** averages completed (not returned) standard and eBay sales.
  * Returns count on the date they happen: a sale in October returned in November adds to October's gross and November's returns. A full return before payout keeps the original sale's date.
* **Sub-second OLAP Aggregations**: The dashboard aggregates volumes dynamically over customized intervals (Day, Week, Month) using DuckDB SQL.
* **Responsive Trend Columns**: Blue bars are gross sales; red bars below the line are returns (each return is counted once). Hovering over a column displays a detailed tooltip showing:
  * Total Sales Volume (Revenue, gross)
  * Total Units Sold
  * Total Commissions Earned
  * Highest Value Single Sale
  * Type and Product Category Distribution, plus returns

### 2.3 Commission Waterfall Engine
Commissions are calculated instantly during entry or import through a user-prioritized waterfall of matching criteria:
* **All Used Gear Rule**: Triggers matching rules for physical equipment marked as *Used Gear* (Defaults to **3%** of Sale Price).
* **All Warranty Rule**: Triggers matching rules for protection packages (Defaults to **10%** of Net Margin/Profit).
* **All eBay Rule**: Triggers matching rules for items sold on eBay (Defaults to **10%** of Sale Price).
* **Category Specific Rules**: Rules tied directly to product lines (e.g., Lenses at **8%**, Mirrorless at **5%**).
* **Payout Calculation Types**: Supports flat rates (e.g. `$25.00`), percentage of price, and percentage of net profit (warranties only: `(SalePrice - ManufacturerPrice) * Rate`).
* **Rule Basis Display**: Each rule card in the waterfall UI explicitly displays what the commission is based on (e.g., "Percentage of Price", "Flat Rate", or "Percentage of Net Margin").
* **Commission Is Stored Per Sale**: Commission is calculated when a sale is saved, and the payout method that produced it (`AppliedCalculationType`) is stored with it so returns know how to claw it back.
* **Rule Changes Apply to the Current Pay Period**: Adding, editing, deleting, or reordering a rule re-runs the rules on **every pending sale** (everything not yet finalized, whatever its date). Paid sales are never changed. Partial returns on pending sales are re-balanced to match; returns against paid sales keep the amount that was actually paid.

### 2.4 CSV Import Wizard
Imports sales records from spreadsheets/spiff sheets in two steps. **Nothing is saved until the final Import button.**

1. **Choose file & map columns**
   * Columns are auto-matched by header name and can be changed. `[None]` marks a column as absent.
   * Sale Price and either SKU or Product Name are required. Importing as "Warranty" swaps the category and used-gear mappings for warranty type and wholesale price.
   * "Category for rows with no category" and "Treat rows as USED unless marked new" decide what happens to **blank** cells.
2. **Review**: every row lands in one of these groups:
   * **Ready**: will be imported as-is.
   * **Needs Fixing**: a value couldn't be understood (unreadable date or price, negative price, unknown category or warranty type, missing value in a mapped column). Bad values are **reported, never guessed**. Rows can be corrected right in the dialog (with pick-lists for category/warranty type); a fixed row is imported with the rest.
   * **Possible Duplicates**: a sale with the same day, price, SKU and invoice number (or product name, if the file has no invoice number) already exists, **or** the row is used gear whose SKU has already been sold. These are **flagged for review, not rejected**: each is skipped unless "Import anyway" is ticked (with Import All / Skip All shortcuts). Rows within the same file are not compared to each other, so identical items on one receipt both import.
   * **Ignored**: totals rows and rows with no SKU or product name, listed by row number.
3. **Import**: all chosen rows are saved in a single transaction (all or nothing).

Problem rows that weren't fixed can be saved with **Save Problem Rows as CSV**, which writes only those rows (original columns plus an `Import Problem` column explaining what's wrong). Fix that file in a spreadsheet and import it on its own; because it holds only the problem rows, it won't duplicate the rows that were already imported. Alternatively, cancel at the review step, fix the original file, and load it again; nothing was saved yet.

Used/new column values: `used`, `yes`, `true`, `1` mean used; `new`, `unused`, `no`, `false`, `0` mean new; anything else uses the "treat as used" setting.

### 2.5 Returns
* **Returns** open a confirmation dialog showing the sale price, what's already been refunded, what remains, and the commission effect. The refund amount defaults to the full remaining amount and can be lowered for a **partial return**.
  * **Full return of a pending sale** (with no earlier returns): the sale is marked `ReturnedBeforePayout` and its commission removed. It can be restored with ↺.
  * **Any other return** (partial returns, or returns on paid sales): a **return line** is added in the current period. It's a record of the same type with `IsReturn = true`, a negative price (the refund), a negative commission (the clawback), and `OriginalSaleId` pointing at the sale. Paid history is never modified.
  * **Several partial returns** can be recorded against one sale, up to its full price. They stay linked through `OriginalSaleId`.
  * **Commission clawback**: percentage-based commission is taken back in proportion to the refund (refund $250 of a $1,000 sale with $80 commission → −$20). The return that completes the refund takes back exactly what's left, so rounding never strands pennies. **Flat-rate** commission is taken back in full by the first return; later returns take back nothing. The total clawed back never exceeds the original commission.
  * **Editing a return line** (✎ on a pending return line) changes its refund amount or date through the same confirmation dialog; the clawback is recalculated. Return lines that have been paid out are locked.
### 2.6 Payout Reports (header → **Payout Reports**)
The app has three pages in the header: **Dashboard**, **Payout Reports**, and **Settings**.

* **Current Pay Period** card: everything the next finalize will include (all unpaid sales and returns, plus items returned before payout that aren't on a report yet, whatever their dates), with items, gross, returns, net and commission payout, when the period opened (the last finalize), and the sale dates it covers.
  * **Preview PDF**: renders the open period to a temporary file and opens it. Nothing is locked or saved to the reports folder.
  * **Finalize Pay Period…**: shows a summary and asks for a report name. Finalizing locks everything as Paid, saves the PDF to the reports folder, and opens it for printing. It's refused if nothing is open.
* **Report History**: every finalized period, newest first, showing when it was **finalized**, the **period it covered** (from the previous finalize to this one, since pay periods don't follow calendar months), the sale dates it held, items, gross, net and payout. Actions on each:
  * **Print**: sends the PDF to Windows' print command. If the default PDF app can't take print commands (e.g. a browser), the PDF is opened instead so it can be printed from there.
  * **View**, **Copy** (puts the PDF on the clipboard to paste into an email or message), **Show in Folder**.
  * **Regenerate**: rebuilds the PDF from the ledger (paid records never change, so the content is the same) and marks it "Reprinted". View/Print/Copy also do this automatically when a PDF is missing (moved, deleted, or a report finalized before reports were stored).
* **Storage**: PDFs are saved as `yyyy-MM-dd <Report Name>.pdf` in `Documents\Sales Ledger Reports` by default (Debug builds use `Sales Ledger Reports (Dev)` so test reports never mix with real ones). Change it under Settings → Identity Profile → **Report Storage**.

### 2.7 PDF Commission Statements (QuestPDF)
* The header shows the representative, when the period was **finalized**, the **sale dates** it covers, and "Reprinted" for regenerated copies (previews are marked "PREVIEW, not finalized").
* The summary uses the same definitions as the dashboard (`PeriodTotals`): Gross Sales, Returns, Net Sales, Units Sold (Net), Avg Sale Price, and Commission Payout (which equals the report's total).
* Sale sections list everything sold (items returned before payout are noted); the **Returns** section lists return lines (partial refunds noted) and items returned before payout. Section subtotals minus returns equal net sales.

### 2.8 Full-Screen Settings
* **Taxonomy Presets**: Configure system-level product categories and warranty brands. Presets are soft-deletable to keep historical transactions intact while pruning future selection choices.
* **Interactive Rules Management**: 
  * ▲ / ▼ buttons on each rule reprioritize its execution order within the waterfall engine.
  * An **Edit** button on each rule card triggers an edit modal overlay allowing direct updates to Rule Name, Scope, Target Category, Payout Method, and Value.
  * After any rule change, a status message reports how many pending sales were recalculated.
* **Report Storage**: Settings → Identity Profile → choose the folder payout report PDFs are saved to, open it, or go back to the default.
* **Database Reset Tool**: Settings → Identity Profile → **Reset Database...** wipes all transactions, reports and settings and rebuilds the DuckDB analytical tables. It is available in installed builds, so it requires typing `RESET` in a confirmation dialog.
* **Test Update UI**: Debug builds display a "Test Update UI" button under Application Updates to allow developers to preview the software update overlay, cancel it, or simulate installation progress and auto-restarts locally.

### 2.9 Application Update Dialog Overlay
When an update is checked (either automatically on startup or manually via Settings) and found:
* A modal dialog overlay appears prompting the user to install the update and states that the application will restart automatically.
* Confirming the update initiates a background download with progress/disabled buttons to prevent UI thread locks.
* Once the update is downloaded, the application automatically restarts using Velopack to apply the new version.

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
│   │   ├── PayoutLedgerService.cs     # Returns (full/partial/edit), deletes, rule recalculation, period close
│   │   ├── ReceiptService.cs          # Loads/saves multi-item receipts without touching locked records; name suggestions
│   │   ├── SkuCheckService.cs         # Finds earlier sales of a used-gear SKU (warnings, never blocks)
│   │   ├── CsvHelper.cs               # CSV parsing and writing
│   │   ├── CsvImportService.cs        # CSV import: parse, validate, duplicate check, transactional import
│   │   ├── AnalyticsService.cs        # duckdb OLAP aggregations for dashboard trends
│   │   ├── PeriodTotals.cs            # Gross/returns/net/units/commission rules shared by PDF and dashboard
│   │   ├── ReportService.cs           # Finalize, report storage folder, regenerate, preview, history
│   │   ├── ShellActions.cs            # Open / print / show in folder / copy-to-clipboard (faked in tests)
│   │   └── ReportGenerator.cs         # PDF reporting using QuestPDF layout definitions
│   │
│   ├── ViewModels/
│   │   ├── MainWindowViewModel.cs     # Creates services, swaps view states & checks update loops
│   │   ├── LedgerDashboardViewModel.cs# Transactions grid, chart data, receipt dialog
│   │   ├── ReceiptLineViewModel.cs    # One editable or read-only line in the receipt dialog
│   │   ├── ReturnDialogViewModel.cs   # Return confirmation: full/partial/edit with commission preview
│   │   ├── CsvImportViewModel.cs      # CSV import wizard (mapping → review → import)
│   │   ├── ReportsViewModel.cs        # Payout Reports page: open period, finalize, history actions
│   │   └── FullScreenSettingsViewModel.cs # Manages presets, rule reordering, and DB resets
│   │
│   └── Views/
│       ├── LedgerDashboardView.axaml  # Grid view, hover action buttons, chart tooltips, receipt dialog
│       ├── ReturnDialogView.axaml     # Return confirmation overlay
│       ├── CsvImportDialogView.axaml  # CSV import wizard overlay
│       ├── ReportsView.axaml          # Payout Reports page
│       └── FullScreenSettingsView.axaml# Tabs for Profile, custom presets, and rule waterfall
│
├── MainWindow.axaml / .cs             # Main app frame
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
*Note: Debug builds automatically target isolated database files (`SalesLedgerDev` directories for LiteDB and DuckDB), safeguarding your installed production database from testing data.*

**Dates** are stored and handled in local time throughout. LiteDB stores dates as UTC and converts them back to local time on read; DuckDB stores the wall-clock value it's given. Always assign `DateTime.Now`-style local values, never `DateTime.UtcNow`, to sale dates, or analytics will be off by the UTC offset until the next startup rebuild.

**Blank text**: LiteDB is configured with `EmptyStringToNull = false`, and model text properties coerce `null` to `""` (older records stored blanks as null), so a blank SKU or invoice never causes a null-reference crash.

### Run Unit Test Suite
Runs all unit and integration tests covering database replication, waterfall logic, returns (full, partial, multiple, edits), receipt editing, rule recalculation, CSV parsing/import, analytics and report totals, plus **headless UI tests** (`SalesLedger.Tests/UiTests`) that drive the real window with simulated clicks and typing, using a throwaway data folder:
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
   Copy the contents of the generated `Releases` folder (containing `Setup.exe`, `.nupkg` assets, and `RELEASES` metadata) to your update storage endpoint (e.g. GitHub Releases, AWS S3, or web host). The app checks the GitHub repository configured as a `GithubSource` in two places, which must be kept in sync:
   * `CheckForApplicationUpdatesAsync` in `SalesLedger/Core/ViewModels/MainWindowViewModel.cs` (startup check)
   * `CheckForUpdatesManual` in `SalesLedger/Core/ViewModels/FullScreenSettingsViewModel.cs` (Settings → Check for Updates)

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


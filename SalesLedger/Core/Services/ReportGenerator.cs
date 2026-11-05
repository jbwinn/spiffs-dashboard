using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SalesLedger.Core.Models;
using SalesLedger.Core.Theme;

namespace SalesLedger.Core.Services
{
    public class ReportGenerator
    {
        static ReportGenerator()
        {
            // Register QuestPDF community license to prevent runtime exceptions
            QuestPDF.Settings.License = LicenseType.Community;
        }

        /// <param name="isPreview">The period hasn't been finalized yet (nothing is locked).</param>
        /// <param name="reprintedAt">Set when an already-finalized report is regenerated later.</param>
        public void GeneratePdfReport(PayoutReport report, List<SaleRecord> sales, UserSettings settings, string outputPath,
            bool isPreview = false, DateTime? reprintedAt = null)
        {
            var saleDates = sales.Where(PeriodTotals.IsSaleRow).Select(s => s.TransactionDate).ToList();
            var coverage = saleDates.Count > 0
                ? $"Sales dated: {saleDates.Min():MMM d, yyyy} – {saleDates.Max():MMM d, yyyy}"
                : "Sales dated: (no sales)";

            var directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.Letter);
                    page.Margin(0.5f, Unit.Inch); // Moderate margins
                    page.PageColor(Colors.White);
                    page.DefaultTextStyle(x => x.FontFamily(AppTheme.PrintFontPrimaryFamilies).FontSize(10).FontColor(Color.FromHex(AppTheme.PrintTextPrimary)));

                    // Header
                    page.Header().Element(headerContainer =>
                    {
                        headerContainer.Row(row =>
                        {
                            row.RelativeItem().Column(column =>
                            {
                                column.Item().Text("COMMISSION PAYOUT REPORT")
                                    .FontSize(18)
                                    .Bold()
                                    .FontColor(Color.FromHex(AppTheme.PrintAccentPrimary));

                                column.Item().Text($"Pay Period: {report.ReportName}")
                                    .FontFamily(AppTheme.PrintFontSecondaryFamilies)
                                    .FontSize(12)
                                    .Bold()
                                    .FontColor(Color.FromHex(AppTheme.PrintTextSecondary));
                            });

                            row.ConstantItem(220).Column(column =>
                            {
                                column.Item().Text($"Representative: {settings.UserDisplayName}").Bold().AlignRight();
                                column.Item().Text(isPreview
                                        ? $"PREVIEW, not finalized: {DateTime.Now:yyyy-MM-dd HH:mm}"
                                        : $"Finalized: {report.ReportGeneratedTimestamp.ToLocalTime():yyyy-MM-dd HH:mm}")
                                    .AlignRight();
                                column.Item().Text(coverage).FontSize(9).AlignRight();
                                if (reprintedAt is { } reprinted)
                                {
                                    column.Item().Text($"Reprinted: {reprinted:yyyy-MM-dd HH:mm}").FontSize(9).AlignRight();
                                }
                            });
                        });
                    });

                    // Content
                    page.Content().PaddingVertical(20).Column(column =>
                    {
                        // Summary: same definitions as the dashboard KPIs (see PeriodTotals)
                        var totals = PeriodTotals.From(sales);

                        column.Item().PaddingBottom(10).Row(row =>
                        {
                            row.RelativeItem().Element(c => ConfigureStatCard(c, "Gross Sales", totals.GrossSales.ToString("C"), "Everything sold, incl. later returns"));
                            row.ConstantItem(15);
                            row.RelativeItem().Element(c => ConfigureStatCard(c, "Returns", (-totals.Returns).ToString("C"), "Refunds in this period"));
                            row.ConstantItem(15);
                            row.RelativeItem().Element(c => ConfigureStatCard(c, "Net Sales", totals.NetSales.ToString("C"), "Gross sales minus returns"));
                        });

                        column.Item().PaddingBottom(20).Row(row =>
                        {
                            row.RelativeItem().Element(c => ConfigureStatCard(c, "Units Sold (Net)", totals.NetUnits.ToString(),
                                $"{totals.GrossUnits} sold · {totals.UnitsReturned} returned"));
                            row.ConstantItem(15);
                            row.RelativeItem().Element(c => ConfigureStatCard(c, "Avg Sale Price", totals.AverageSalePrice.ToString("C"), "All sale types, items kept"));
                            row.ConstantItem(15);
                            row.RelativeItem().Element(c => ConfigureStatCard(c, "Commission Payout", totals.Commission.ToString("C"), "After return adjustments"));
                        });

                        // Sales Details Table title
                        column.Item().PaddingBottom(8).Text("Transaction Ledger Details").FontSize(14).Bold().FontColor(Color.FromHex(AppTheme.PrintAccentPrimary));

                        void RenderSection(string title, List<ReportRow> sectionSales)
                        {
                            if (!sectionSales.Any()) return;

                            column.Item().PaddingTop(12).PaddingBottom(4).Text(title).FontFamily(AppTheme.PrintFontSecondaryFamilies).FontSize(11).Bold().FontColor(Color.FromHex(AppTheme.PrintAccentSecondary));

                            column.Item().Table(table =>
                            {
                                // Define columns
                                table.ColumnsDefinition(columns =>
                                {
                                    columns.ConstantColumn(80);  // Date
                                    columns.ConstantColumn(85);  // Invoice / Order Code
                                    columns.RelativeColumn(3);   // Product Name
                                    columns.RelativeColumn(2);   // Category
                                    columns.RelativeColumn(2);   // Price
                                    columns.RelativeColumn(2);   // Commission
                                });

                                // Headers
                                table.Header(header =>
                                {
                                    header.Cell().Element(ConfigureHeaderCell).Text("Date").Bold();
                                    header.Cell().Element(ConfigureHeaderCell).Text(title.Contains("eBay") ? "Order Code" : "Invoice").Bold();
                                    header.Cell().Element(ConfigureHeaderCell).Text("Product").Bold();
                                    header.Cell().Element(ConfigureHeaderCell).Text("Category").Bold();
                                    header.Cell().Element(ConfigureHeaderCell).Text("Price").Bold().AlignRight();
                                    header.Cell().Element(ConfigureHeaderCell).Text("Commission").Bold().AlignRight();
                                });

                                // Rows
                                bool isAlternate = false;
                                foreach (var row in sectionSales.OrderBy(s => s.Date))
                                {
                                    bool currentAlternate = isAlternate;
                                    table.Cell().Element(c => ConfigureCell(c, currentAlternate)).Text(row.Date.ToString("yyyy-MM-dd"));
                                    table.Cell().Element(c => ConfigureCell(c, currentAlternate)).Text(row.Invoice);
                                    table.Cell().Element(c => ConfigureCell(c, currentAlternate)).Text(row.Product);
                                    table.Cell().Element(c => ConfigureCell(c, currentAlternate)).Text(row.Category);
                                    table.Cell().Element(c => ConfigureCell(c, currentAlternate)).AlignRight().Text(row.Price.ToString("C"));
                                    table.Cell().Element(c => ConfigureCell(c, currentAlternate)).AlignRight().Text(row.Commission.ToString("C"));

                                    isAlternate = !isAlternate;
                                }

                                // Subtotals
                                var subTotalSales = sectionSales.Sum(s => s.Price);
                                var subTotalComm = sectionSales.Sum(s => s.Commission);

                                table.Cell().Element(ConfigureSubtotalCell).Text(string.Empty);
                                table.Cell().Element(ConfigureSubtotalCell).Text(string.Empty);
                                table.Cell().Element(ConfigureSubtotalCell).Text("Subtotal").Bold();
                                table.Cell().Element(ConfigureSubtotalCell).Text(string.Empty);
                                table.Cell().Element(ConfigureSubtotalCell).AlignRight().Text(subTotalSales.ToString("C")).Bold();
                                table.Cell().Element(ConfigureSubtotalCell).AlignRight().Text(subTotalComm.ToString("C")).Bold();
                            });
                        }

                        // Sale sections are gross: items returned before payout are listed (and noted) in their section,
                        // and again as a refund under Returns, so section subtotals minus returns equals net sales.
                        List<ReportRow> SalesOf(System.Func<SaleRecord, bool> filter) =>
                            sales.Where(s => PeriodTotals.IsSaleRow(s) && filter(s)).Select(ReportRow.ForSale).ToList();

                        RenderSection("Standard Sales (New Gear)", SalesOf(s => s is StandardSale { IsUsedGear: false }));
                        RenderSection("Standard Sales (Used Gear)", SalesOf(s => s is StandardSale { IsUsedGear: true }));
                        RenderSection("eBay Sales", SalesOf(s => s is EbaySale));
                        RenderSection("Warranty Sales", SalesOf(s => s is WarrantySale));
                        RenderSection("Returns",
                            sales.Where(PeriodTotals.IsReturnLine).Select(ReportRow.ForReturnLine)
                                .Concat(sales.Where(s => PeriodTotals.IsSaleRow(s) && s.Status == PayoutStatus.ReturnedBeforePayout).Select(ReportRow.ForReturnedBeforePayout))
                                .ToList());
                    });

                    // Footer
                    page.Footer().AlignCenter().Text(x =>
                    {
                        x.CurrentPageNumber();
                        x.Span(" / ");
                        x.TotalPages();
                    });
                });
            }).GeneratePdf(outputPath);
        }

        /// <summary>One line in a report table.</summary>
        private sealed record ReportRow(DateTime Date, string Invoice, string Product, string Category, decimal Price, decimal Commission)
        {
            public static ReportRow ForSale(SaleRecord s) => new(
                s.TransactionDate, s.InvoiceNumber,
                s.Status == PayoutStatus.ReturnedBeforePayout ? $"{s.ProductName} (returned before payout)" : s.ProductName,
                s.Category, s.SalePrice, s.CalculatedCommission);

            public static ReportRow ForReturnLine(SaleRecord s) => new(
                s.TransactionDate, s.InvoiceNumber,
                s.IsReturn && !s.CompletesRefund ? $"{s.ProductName} (partial refund)" : s.ProductName,
                s.Category, s.SalePrice, s.CalculatedCommission);

            // The sale's commission was already zeroed, so the refund carries no further commission change
            public static ReportRow ForReturnedBeforePayout(SaleRecord s) => new(
                s.TransactionDate, s.InvoiceNumber, $"[RETURNED BEFORE PAYOUT] - {s.ProductName}",
                s.Category, -s.SalePrice, 0m);
        }

        private void ConfigureStatCard(IContainer container, string title, string value, string? caption = null)
        {
            container
                .Border(1)
                .BorderColor(Color.FromHex(AppTheme.PrintBorder))
                .Background(Color.FromHex(AppTheme.PrintBgCard))
                .Padding(10)
                .Column(col =>
                {
                    col.Item().Text(title).FontSize(9).FontColor(Color.FromHex(AppTheme.PrintTextSecondary)).Bold();
                    col.Item().PaddingTop(4).Text(value).FontSize(14).Bold().FontColor(Color.FromHex(AppTheme.PrintAccentPrimary));
                    if (!string.IsNullOrEmpty(caption))
                    {
                        col.Item().PaddingTop(2).Text(caption).FontSize(7).FontColor(Color.FromHex(AppTheme.PrintTextSecondary));
                    }
                });
        }

        private IContainer ConfigureHeaderCell(IContainer container)
        {
            return container
                .BorderBottom(1)
                .BorderColor(Color.FromHex(AppTheme.PrintTextSecondary))
                .PaddingVertical(5)
                .PaddingHorizontal(2);
        }

        private IContainer ConfigureCell(IContainer container, bool isAlternate)
        {
            return container
                .BorderBottom(1)
                .BorderColor(Color.FromHex(AppTheme.PrintBorder))
                .Background(isAlternate ? Color.FromHex(AppTheme.PrintBgLight) : Colors.White)
                .PaddingVertical(4)
                .PaddingHorizontal(2);
        }

        private IContainer ConfigureSubtotalCell(IContainer container)
        {
            return container
                .BorderTop(1)
                .BorderColor(Color.FromHex(AppTheme.PrintTextSecondary))
                .PaddingVertical(4)
                .PaddingHorizontal(2)
                .Background(Color.FromHex(AppTheme.PrintBgCard));
        }
    }
}

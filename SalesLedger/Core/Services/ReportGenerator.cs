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

        public void GeneratePdfReport(PayoutReport report, List<SaleRecord> sales, UserSettings settings, string outputPath)
        {
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

                            row.ConstantItem(200).Column(column =>
                            {
                                column.Item().Text($"Representative: {settings.UserDisplayName}").Bold().AlignRight();
                                column.Item().Text($"Generated: {report.ReportGeneratedTimestamp.ToLocalTime():yyyy-MM-dd HH:mm}").AlignRight();
                            });
                        });
                    });

                    // Content
                    page.Content().PaddingVertical(20).Column(column =>
                    {
                        // Summary Stats Grid
                        column.Item().PaddingBottom(20).Row(row =>
                        {
                            var totalSales = sales.Sum(s => s.SalePrice);
                            var totalCommission = sales.Sum(s => s.CalculatedCommission);
                            var unitsSold = sales.Count(s => s.RecordType == SaleType.Standard || s.RecordType == SaleType.Ebay) - sales.Count(s => s.RecordType == SaleType.ReturnOffset);
                            var standardSales = sales.Where(s => (s.RecordType == SaleType.Standard || s.RecordType == SaleType.Ebay) && s.SalePrice > 0).ToList();
                            var asp = standardSales.Any() ? standardSales.Average(s => s.SalePrice) : 0m;

                            row.RelativeItem().Element(c => ConfigureStatCard(c, "Total Sales", totalSales.ToString("C")));
                            row.ConstantItem(15);
                            row.RelativeItem().Element(c => ConfigureStatCard(c, "Units Sold", unitsSold.ToString()));
                            row.ConstantItem(15);
                            row.RelativeItem().Element(c => ConfigureStatCard(c, "Total Commission", totalCommission.ToString("C")));
                            row.ConstantItem(15);
                            row.RelativeItem().Element(c => ConfigureStatCard(c, "Avg Sale Price", asp.ToString("C")));
                        });

                        // Sales Details Table title
                        column.Item().PaddingBottom(8).Text("Transaction Ledger Details").FontSize(14).Bold().FontColor(Color.FromHex(AppTheme.PrintAccentPrimary));

                        void RenderSection(string title, List<SaleRecord> sectionSales)
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
                                foreach (var sale in sectionSales.OrderBy(s => s.TransactionDate))
                                {
                                    table.Cell().Element(c => ConfigureCell(c, isAlternate)).Text(sale.TransactionDate.ToString("yyyy-MM-dd"));
                                    table.Cell().Element(c => ConfigureCell(c, isAlternate)).Text(sale.InvoiceNumber);
                                    table.Cell().Element(c => ConfigureCell(c, isAlternate)).Text(sale.ProductName);
                                    table.Cell().Element(c => ConfigureCell(c, isAlternate)).Text(sale.Category);
                                    table.Cell().Element(c => ConfigureCell(c, isAlternate)).AlignRight().Text(sale.SalePrice.ToString("C"));
                                    table.Cell().Element(c => ConfigureCell(c, isAlternate)).AlignRight().Text(sale.CalculatedCommission.ToString("C"));

                                    isAlternate = !isAlternate;
                                }

                                // Subtotals
                                var subTotalSales = sectionSales.Sum(s => s.SalePrice);
                                var subTotalComm = sectionSales.Sum(s => s.CalculatedCommission);

                                table.Cell().Element(ConfigureSubtotalCell).Text(string.Empty);
                                table.Cell().Element(ConfigureSubtotalCell).Text(string.Empty);
                                table.Cell().Element(ConfigureSubtotalCell).Text("Subtotal").Bold();
                                table.Cell().Element(ConfigureSubtotalCell).Text(string.Empty);
                                table.Cell().Element(ConfigureSubtotalCell).AlignRight().Text(subTotalSales.ToString("C")).Bold();
                                table.Cell().Element(ConfigureSubtotalCell).AlignRight().Text(subTotalComm.ToString("C")).Bold();
                            });
                        }

                        RenderSection("Standard Sales (New Gear)", sales.Where(s => s is StandardSale std && !std.IsUsedGear).Cast<SaleRecord>().ToList());
                        RenderSection("Standard Sales (Used Gear)", sales.Where(s => s is StandardSale std && std.IsUsedGear).Cast<SaleRecord>().ToList());
                        RenderSection("eBay Sales", sales.Where(s => s is EbaySale).ToList());
                        RenderSection("Warranty Sales", sales.Where(s => s is WarrantySale).ToList());
                        RenderSection("Return Offsets", sales.Where(s => s is ReturnOffsetSale).ToList());
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

        private void ConfigureStatCard(IContainer container, string title, string value)
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

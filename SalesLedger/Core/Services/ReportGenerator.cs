using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SalesLedger.Core.Models;

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
                    page.DefaultTextStyle(x => x.FontFamily("Arial").FontSize(10));

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
                                    .FontColor(Colors.Blue.Darken3);

                                column.Item().Text($"Pay Period: {report.ReportName}")
                                    .FontSize(12)
                                    .Bold()
                                    .FontColor(Colors.Grey.Darken2);
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
                            var unitsSold = sales.Count(s => s.RecordType == SaleType.Standard) - sales.Count(s => s.RecordType == SaleType.ReturnOffset);
                            var standardSales = sales.Where(s => s.RecordType == SaleType.Standard && s.SalePrice > 0).ToList();
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
                        column.Item().PaddingBottom(8).Text("Transaction Ledger Details").FontSize(14).Bold().FontColor(Colors.Blue.Darken3);

                        // Table
                        column.Item().Table(table =>
                        {
                            // Define columns
                            table.ColumnsDefinition(columns =>
                            {
                                columns.ConstantColumn(80);  // Date
                                columns.ConstantColumn(80);  // Invoice
                                columns.RelativeColumn(3);   // Product Name
                                columns.RelativeColumn(2);   // Category
                                columns.RelativeColumn(2);   // Price
                                columns.RelativeColumn(2);   // Commission
                            });

                            // Headers
                            table.Header(header =>
                            {
                                header.Cell().Element(ConfigureHeaderCell).Text("Date").Bold();
                                header.Cell().Element(ConfigureHeaderCell).Text("Invoice").Bold();
                                header.Cell().Element(ConfigureHeaderCell).Text("Product").Bold();
                                header.Cell().Element(ConfigureHeaderCell).Text("Category").Bold();
                                header.Cell().Element(ConfigureHeaderCell).Text("Price").Bold().AlignRight();
                                header.Cell().Element(ConfigureHeaderCell).Text("Commission").Bold().AlignRight();
                            });

                            // Rows
                            bool isAlternate = false;
                            foreach (var sale in sales.OrderBy(s => s.TransactionDate))
                            {
                                table.Cell().Element(c => ConfigureCell(c, isAlternate)).Text(sale.TransactionDate.ToString("yyyy-MM-dd"));
                                table.Cell().Element(c => ConfigureCell(c, isAlternate)).Text(sale.InvoiceNumber);
                                table.Cell().Element(c => ConfigureCell(c, isAlternate)).Text(sale.ProductName);
                                table.Cell().Element(c => ConfigureCell(c, isAlternate)).Text(sale.Category);
                                table.Cell().Element(c => ConfigureCell(c, isAlternate)).AlignRight().Text(sale.SalePrice.ToString("C"));
                                table.Cell().Element(c => ConfigureCell(c, isAlternate)).AlignRight().Text(sale.CalculatedCommission.ToString("C"));

                                isAlternate = !isAlternate;
                            }
                        });
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
                .BorderColor(Colors.Grey.Lighten2)
                .Background(Colors.Grey.Lighten4)
                .Padding(10)
                .Column(col =>
                {
                    col.Item().Text(title).FontSize(9).FontColor(Colors.Grey.Darken1).Bold();
                    col.Item().PaddingTop(4).Text(value).FontSize(14).Bold().FontColor(Colors.Blue.Darken3);
                });
        }

        private IContainer ConfigureHeaderCell(IContainer container)
        {
            return container
                .BorderBottom(1)
                .BorderColor(Colors.Grey.Darken1)
                .PaddingVertical(5)
                .PaddingHorizontal(2);
        }

        private IContainer ConfigureCell(IContainer container, bool isAlternate)
        {
            return container
                .BorderBottom(1)
                .BorderColor(Colors.Grey.Lighten3)
                .Background(isAlternate ? Colors.Grey.Lighten5 : Colors.White)
                .PaddingVertical(4)
                .PaddingHorizontal(2);
        }
    }
}

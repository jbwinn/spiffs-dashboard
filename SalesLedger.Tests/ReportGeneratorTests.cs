using System;
using System.Collections.Generic;
using System.IO;
using Xunit;
using SalesLedger.Core.Models;
using SalesLedger.Core.Services;

namespace SalesLedger.Tests
{
    public class ReportGeneratorTests : IDisposable
    {
        private readonly string _outputPath;

        public ReportGeneratorTests()
        {
            _outputPath = Path.Combine(Path.GetTempPath(), $"test_report_{Guid.NewGuid():N}.pdf");
        }

        [Fact]
        public void GeneratePdfReport_CreatesFileOnDisk()
        {
            var report = new PayoutReport
            {
                Id = Guid.NewGuid(),
                ReportName = "Test Submission July 2026",
                TotalCommissionCalculated = 125.00m,
                ReportGeneratedTimestamp = DateTime.UtcNow
            };

            var sales = new List<SaleRecord>
            {
                new StandardSale
                {
                    InvoiceNumber = "INV-100",
                    ProductName = "Camera ABC",
                    Category = "Cameras",
                    SalePrice = 1000m,
                    CalculatedCommission = 50.00m,
                    TransactionDate = DateTime.UtcNow.AddDays(-2)
                },
                new WarrantySale
                {
                    InvoiceNumber = "INV-101",
                    ProductName = "Warranty Extension",
                    Category = "Accessories",
                    SalePrice = 150m,
                    CalculatedCommission = 25.00m,
                    WarrantyTypeName = "3-Year Extension",
                    ManufacturerPrice = 50m,
                    TransactionDate = DateTime.UtcNow.AddDays(-1)
                },
                new ReturnOffsetSale
                {
                    InvoiceNumber = "INV-100",
                    ProductName = "[RETURN OFFSET] - Camera ABC",
                    Category = "Cameras",
                    SalePrice = -1000m,
                    CalculatedCommission = -50.00m,
                    TransactionDate = DateTime.UtcNow
                }
            };

            var settings = new UserSettings
            {
                UserDisplayName = "John Doe"
            };

            var generator = new ReportGenerator();
            generator.GeneratePdfReport(report, sales, settings, _outputPath);

            Assert.True(File.Exists(_outputPath));
            Assert.True(new FileInfo(_outputPath).Length > 0);
        }

        public void Dispose()
        {
            try
            {
                if (File.Exists(_outputPath))
                {
                    File.Delete(_outputPath);
                }
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }
}

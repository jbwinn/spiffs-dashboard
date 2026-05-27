using System;
using System.IO;
using Xunit;
using SalesLedger.Core.Services;

namespace SalesLedger.Tests
{
    public class CsvHelperTests
    {
        [Fact]
        public void ParseCsv_SimpleData_ParsesCorrectly()
        {
            // Arrange
            string csvContent = "Date,Receipt Number,Product Name,Price\n1/2/2026,1001,Sony a7 IV,1800.00\n1/3/2026,1002,Canon EOS R5,3000.00";
            string tempFile = Path.GetTempFileName();
            File.WriteAllText(tempFile, csvContent);

            try
            {
                // Act
                var rows = CsvHelper.ParseCsv(tempFile);

                // Assert
                Assert.Equal(3, rows.Count);
                Assert.Equal("Date", rows[0][0]);
                Assert.Equal("1/2/2026", rows[1][0]);
                Assert.Equal("1001", rows[1][1]);
                Assert.Equal("Sony a7 IV", rows[1][2]);
                Assert.Equal("1800.00", rows[1][3]);
                
                Assert.Equal("1/3/2026", rows[2][0]);
                Assert.Equal("Canon EOS R5", rows[2][2]);
            }
            finally
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
        }

        [Fact]
        public void ParseCsv_QuotedValuesWithEmbeddedCommas_ParsesCorrectly()
        {
            // Arrange
            string csvContent = "Date,Receipt,Warranty Type,Price\n1/9/2026,13960,\"$2,001-2,500 2 YR DOP w/ ADH Mirrorless Cameras & DSLR\",$169.99";
            string tempFile = Path.GetTempFileName();
            File.WriteAllText(tempFile, csvContent);

            try
            {
                // Act
                var rows = CsvHelper.ParseCsv(tempFile);

                // Assert
                Assert.Equal(2, rows.Count);
                Assert.Equal("Date", rows[0][0]);
                Assert.Equal("1/9/2026", rows[1][0]);
                Assert.Equal("13960", rows[1][1]);
                Assert.Equal("$2,001-2,500 2 YR DOP w/ ADH Mirrorless Cameras & DSLR", rows[1][2]);
                Assert.Equal("$169.99", rows[1][3]);
            }
            finally
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
        }

        [Fact]
        public void ParseCsv_EmptyLinesAndStrayQuotes_SkipsEmptyRowsAndCleansQuotes()
        {
            // Arrange
            string csvContent = "\"\nDate,Receipt\n\n1/2/2026,1001\n,,,\n1/3/2026,1002";
            string tempFile = Path.GetTempFileName();
            File.WriteAllText(tempFile, csvContent);

            try
            {
                // Act
                var rows = CsvHelper.ParseCsv(tempFile);

                // Assert
                Assert.Equal(3, rows.Count);
                Assert.Equal("Date", rows[0][0]);
                Assert.Equal("Receipt", rows[0][1]);
                Assert.Equal("1/2/2026", rows[1][0]);
                Assert.Equal("1001", rows[1][1]);
                Assert.Equal("1/3/2026", rows[2][0]);
                Assert.Equal("1002", rows[2][1]);
            }
            finally
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
        }

        [Fact]
        public void ParseCsv_NewlineInQuotesHeader_ParsesCorrectly()
        {
            // Arrange
            string csvContent = "\"\nDate\",Receipt Number,Price\n1/2/2026,1001,1800.00";
            string tempFile = Path.GetTempFileName();
            File.WriteAllText(tempFile, csvContent);

            try
            {
                // Act
                var rows = CsvHelper.ParseCsv(tempFile);

                // Assert
                Assert.Equal(2, rows.Count);
                Assert.Equal("Date", rows[0][0]);
                Assert.Equal("Receipt Number", rows[0][1]);
                Assert.Equal("Price", rows[0][2]);
                Assert.Equal("1/2/2026", rows[1][0]);
                Assert.Equal("1001", rows[1][1]);
                Assert.Equal("1800.00", rows[1][2]);
            }
            finally
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
        }
    }
}

using System;
using System.Data;
using System.IO;
using DuckDB.NET.Data;
using SalesLedger.Core.Models;

namespace SalesLedger.Core.Services
{
    public class DuckDbService : IDisposable
    {
        private readonly string _dbPath;
        private readonly string _connectionString;

        public DuckDbService()
        {
            var appDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SalesLedger"
            );
            Directory.CreateDirectory(appDataDir);
            _dbPath = Path.Combine(appDataDir, "analytics.duckdb");
            _connectionString = $"Data Source={_dbPath}";
            InitializeTable();
        }

        public DuckDbService(string customDbPath)
        {
            _dbPath = customDbPath;
            var directory = Path.GetDirectoryName(customDbPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            _connectionString = $"Data Source={_dbPath}";
            InitializeTable();
        }

        public DuckDBConnection CreateConnection()
        {
            var conn = new DuckDBConnection(_connectionString);
            if (conn.State != ConnectionState.Open)
            {
                conn.Open();
            }
            return conn;
        }

        private void InitializeTable()
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS sales (
                    Id VARCHAR PRIMARY KEY,
                    RecordType VARCHAR,
                    Status VARCHAR,
                    TransactionDate TIMESTAMP,
                    InvoiceNumber VARCHAR,
                    Sku VARCHAR,
                    ProductName VARCHAR,
                    Category VARCHAR,
                    SalePrice DOUBLE,
                    CalculatedCommission DOUBLE,
                    IsUsedGear BOOLEAN,
                    WarrantyTypeName VARCHAR,
                    ManufacturerPrice DOUBLE,
                    NetMargin DOUBLE,
                    AssociatedReportId VARCHAR
                );";
            cmd.ExecuteNonQuery();
        }

        public void UpsertSale(SaleRecord sale)
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            
            // Extract standard sale parameters
            bool isUsedGear = false;
            if (sale is StandardSale standardSale)
            {
                isUsedGear = standardSale.IsUsedGear;
            }

            // Extract warranty sale parameters
            string warrantyTypeName = string.Empty;
            double manufacturerPrice = 0.0;
            double netMargin = 0.0;
            if (sale is WarrantySale warrantySale)
            {
                warrantyTypeName = warrantySale.WarrantyTypeName;
                manufacturerPrice = (double)warrantySale.ManufacturerPrice;
                netMargin = (double)warrantySale.NetMargin;
            }

            cmd.CommandText = @"
                INSERT INTO sales (
                    Id, RecordType, Status, TransactionDate, InvoiceNumber, Sku, ProductName, 
                    Category, SalePrice, CalculatedCommission, IsUsedGear, WarrantyTypeName, 
                    ManufacturerPrice, NetMargin, AssociatedReportId
                ) VALUES (
                    $id, $recordType, $status, $transactionDate, $invoiceNumber, $sku, $productName, 
                    $category, $salePrice, $calculatedCommission, $isUsedGear, $warrantyTypeName, 
                    $manufacturerPrice, $netMargin, $associatedReportId
                ) ON CONFLICT (Id) DO UPDATE SET 
                    RecordType = excluded.RecordType,
                    Status = excluded.Status,
                    TransactionDate = excluded.TransactionDate,
                    InvoiceNumber = excluded.InvoiceNumber,
                    Sku = excluded.Sku,
                    ProductName = excluded.ProductName,
                    Category = excluded.Category,
                    SalePrice = excluded.SalePrice,
                    CalculatedCommission = excluded.CalculatedCommission,
                    IsUsedGear = excluded.IsUsedGear,
                    WarrantyTypeName = excluded.WarrantyTypeName,
                    ManufacturerPrice = excluded.ManufacturerPrice,
                    NetMargin = excluded.NetMargin,
                    AssociatedReportId = excluded.AssociatedReportId;";

            cmd.Parameters.Add(new DuckDBParameter("id", sale.Id.ToString()));
            cmd.Parameters.Add(new DuckDBParameter("recordType", sale.RecordType.ToString()));
            cmd.Parameters.Add(new DuckDBParameter("status", sale.Status.ToString()));
            cmd.Parameters.Add(new DuckDBParameter("transactionDate", sale.TransactionDate));
            cmd.Parameters.Add(new DuckDBParameter("invoiceNumber", sale.InvoiceNumber ?? string.Empty));
            cmd.Parameters.Add(new DuckDBParameter("sku", sale.Sku ?? string.Empty));
            cmd.Parameters.Add(new DuckDBParameter("productName", sale.ProductName ?? string.Empty));
            cmd.Parameters.Add(new DuckDBParameter("category", sale.Category ?? string.Empty));
            cmd.Parameters.Add(new DuckDBParameter("salePrice", (double)sale.SalePrice));
            cmd.Parameters.Add(new DuckDBParameter("calculatedCommission", (double)sale.CalculatedCommission));
            cmd.Parameters.Add(new DuckDBParameter("isUsedGear", isUsedGear));
            cmd.Parameters.Add(new DuckDBParameter("warrantyTypeName", warrantyTypeName));
            cmd.Parameters.Add(new DuckDBParameter("manufacturerPrice", manufacturerPrice));
            cmd.Parameters.Add(new DuckDBParameter("netMargin", netMargin));
            cmd.Parameters.Add(new DuckDBParameter("associatedReportId", sale.AssociatedReportId?.ToString() ?? string.Empty));

            cmd.ExecuteNonQuery();
        }

        public void DeleteSale(Guid saleId)
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM sales WHERE Id = $id;";
            cmd.Parameters.Add(new DuckDBParameter("id", saleId.ToString()));
            cmd.ExecuteNonQuery();
        }

        public void ClearAll()
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM sales;";
            cmd.ExecuteNonQuery();
        }

        public void Dispose()
        {
            // DuckDB.NET connection pooling and static storage will clean up files, 
            // but we can release structures here if necessary.
        }
    }
}

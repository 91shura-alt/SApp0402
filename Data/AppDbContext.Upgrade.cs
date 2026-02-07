using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System;

namespace SellerOps.App.Data
{
    public partial class AppDbContext
    {
        public void EnsureUpgrade()
        {
            // Если база новая — EF создаст схему.
            // ВАЖНО: если база уже существует, EnsureCreated НЕ добавит новые таблицы/колонки.
            Database.EnsureCreated();

            using var con = new SqliteConnection($"Data Source={DbPath}");
            con.Open();

            using var tx = con.BeginTransaction();

            // ----------------------------
            // Таблицы (на случай, если БД старая и этих таблиц ещё не было)
            // ----------------------------

            EnsureTable(con, "WbProducts", @"
CREATE TABLE IF NOT EXISTS ""WbProducts"" (
    ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_WbProducts"" PRIMARY KEY AUTOINCREMENT,
    ""NmId"" INTEGER NOT NULL,
    ""Article"" TEXT NULL,
    ""Title"" TEXT NULL,
    ""Brand"" TEXT NULL,
    ""Subject"" TEXT NULL,
    ""VendorCode"" TEXT NULL,
    ""Barcode"" TEXT NULL,
    ""IsArchived"" INTEGER NOT NULL DEFAULT 0,
    ""LengthCm"" NUMERIC NULL,
    ""WidthCm"" NUMERIC NULL,
    ""HeightCm"" NUMERIC NULL,
    ""WeightKg"" NUMERIC NULL,
    ""VolumeM3"" NUMERIC NULL,
    ""SyncedAt"" TEXT NULL
);");

            EnsureTable(con, "WbFbsOrders", @"
CREATE TABLE IF NOT EXISTS ""WbFbsOrders"" (
    ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_WbFbsOrders"" PRIMARY KEY AUTOINCREMENT,
    ""OrderId"" INTEGER NOT NULL,
    ""Status"" TEXT NULL,
    ""OrderUid"" TEXT NULL,
    ""Rid"" TEXT NULL,
    ""SupplyId"" TEXT NULL,
    ""CreatedAt"" TEXT NULL,
    ""Article"" TEXT NULL,
    ""NmId"" INTEGER NULL,
    ""ChrtId"" INTEGER NULL,
    ""WarehouseId"" INTEGER NULL,
    ""OfficeId"" INTEGER NULL,
    ""Price"" INTEGER NULL,
    ""FinalPrice"" INTEGER NULL,
    ""CurrencyCode"" INTEGER NULL,
    ""DeliveryType"" TEXT NULL,
    ""Comment"" TEXT NULL,
    ""RawJson"" TEXT NOT NULL DEFAULT '',
    ""ImportedAtUtc"" TEXT NOT NULL
);");

            EnsureTable(con, "WbRealizationLines", @"
CREATE TABLE IF NOT EXISTS ""WbRealizationLines"" (
    ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_WbRealizationLines"" PRIMARY KEY AUTOINCREMENT,
    ""RrdId"" INTEGER NOT NULL,
    ""RrDt"" TEXT NULL,
    ""NmId"" INTEGER NULL,
    ""SupplierArticle"" TEXT NULL,
    ""TechSize"" TEXT NULL,
    ""Barcode"" TEXT NULL,
    ""WarehouseName"" TEXT NULL,
    ""Srid"" TEXT NULL,
    ""DocTypeName"" TEXT NULL,
    ""SupplierOperName"" TEXT NULL,
    ""Quantity"" INTEGER NULL,
    ""PriceWithDiscRub"" NUMERIC NULL,
    ""PpvzForPay"" NUMERIC NULL,
    ""PpvzSalesCommission"" NUMERIC NULL,
    ""DeliveryRub"" NUMERIC NULL,
    ""StorageFee"" NUMERIC NULL,
    ""Deduction"" NUMERIC NULL,
    ""Penalty"" NUMERIC NULL,
    ""CreateDt"" TEXT NULL,
    ""CancelDt"" TEXT NULL
);");

            EnsureTable(con, "WbStockSnapshots", @"
CREATE TABLE IF NOT EXISTS ""WbStockSnapshots"" (
    ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_WbStockSnapshots"" PRIMARY KEY AUTOINCREMENT,
    ""SnapshotAt"" TEXT NOT NULL,
    ""Warehouse"" TEXT NULL,
    ""NmId"" INTEGER NULL,
    ""Barcode"" TEXT NULL,
    ""Quantity"" INTEGER NULL
);");

            EnsureTable(con, "WbRawFiles", @"
CREATE TABLE IF NOT EXISTS ""WbRawFiles"" (
    ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_WbRawFiles"" PRIMARY KEY AUTOINCREMENT,
    ""Kind"" TEXT NOT NULL,
    ""FileName"" TEXT NOT NULL,
    ""CreatedAtUtc"" TEXT NOT NULL,
    ""Json"" TEXT NOT NULL
);");

            EnsureTable(con, "WbImportLogs", @"
CREATE TABLE IF NOT EXISTS ""WbImportLogs"" (
    ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_WbImportLogs"" PRIMARY KEY AUTOINCREMENT,
    ""Kind"" TEXT NOT NULL,
    ""Day"" TEXT NOT NULL,
    ""ImportedAtUtc"" TEXT NOT NULL,
    ""IsComplete"" INTEGER NOT NULL DEFAULT 0,
    ""AddedRows"" INTEGER NOT NULL DEFAULT 0,
    ""MaxRrdId"" INTEGER NOT NULL DEFAULT 0
);");

            EnsureTable(con, "SyncStates", @"
CREATE TABLE IF NOT EXISTS ""SyncStates"" (
    ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_SyncStates"" PRIMARY KEY AUTOINCREMENT,
    ""Key"" TEXT NOT NULL,
    ""LastSyncUtc"" TEXT NULL,
    ""LastValueText"" TEXT NULL
);");

            // ----------------------------
            // Колонки (добавляем всё, что могло появиться в моделях позже)
            // ----------------------------

            // --- WbProducts ---
            // ВНИМАНИЕ: модель WbProduct содержит поля, которых может не быть в старых БД.
            // Поэтому гарантируем наличие всех колонок, которые используются в UI/сервисах.
            EnsureColumn(con, "WbProducts", "Title", "TEXT");
            EnsureColumn(con, "WbProducts", "Brand", "TEXT");
            EnsureColumn(con, "WbProducts", "Subject", "TEXT");
            EnsureColumn(con, "WbProducts", "Article", "TEXT");
            EnsureColumn(con, "WbProducts", "VendorCode", "TEXT");
            EnsureColumn(con, "WbProducts", "Barcode", "TEXT");
            EnsureColumn(con, "WbProducts", "IsArchived", "INTEGER");

            EnsureColumn(con, "WbProducts", "LengthCm", "NUMERIC");
            EnsureColumn(con, "WbProducts", "WidthCm", "NUMERIC");
            EnsureColumn(con, "WbProducts", "HeightCm", "NUMERIC");
            EnsureColumn(con, "WbProducts", "WeightKg", "NUMERIC");
            EnsureColumn(con, "WbProducts", "VolumeM3", "NUMERIC");

            // ✅ критично: используется в ProductsPage/AnalyticsService
            EnsureColumn(con, "WbProducts", "Cost", "NUMERIC");

            EnsureColumn(con, "WbProducts", "SyncedAt", "TEXT");
            EnsureColumn(con, "WbProducts", "BarcodesJson", "TEXT");
            EnsureColumn(con, "WbProducts", "RawJson", "TEXT");

            FixTextNullToEmpty(con, "WbProducts", "Title");
            FixTextNullToEmpty(con, "WbProducts", "BarcodesJson");
            FixTextNullToEmpty(con, "WbProducts", "RawJson");

            // --- WbFbsOrders (Marketplace) ---
            // ВНИМАНИЕ: модель WbFbsOrder сильно расширена; если колонок нет — SaveChanges падает.
            EnsureColumn(con, "WbFbsOrders", "Status", "TEXT");
            EnsureColumn(con, "WbFbsOrders", "SupplierStatus", "TEXT");
            EnsureColumn(con, "WbFbsOrders", "WbStatus", "TEXT");

            EnsureColumn(con, "WbFbsOrders", "OrderUid", "TEXT");
            EnsureColumn(con, "WbFbsOrders", "Rid", "TEXT");
            EnsureColumn(con, "WbFbsOrders", "SupplyId", "TEXT");
            EnsureColumn(con, "WbFbsOrders", "CreatedAt", "TEXT");
            EnsureColumn(con, "WbFbsOrders", "Article", "TEXT");

            EnsureColumn(con, "WbFbsOrders", "NmId", "INTEGER");
            EnsureColumn(con, "WbFbsOrders", "ChrtId", "INTEGER");
            EnsureColumn(con, "WbFbsOrders", "WarehouseId", "INTEGER");
            EnsureColumn(con, "WbFbsOrders", "OfficeId", "INTEGER");

            EnsureColumn(con, "WbFbsOrders", "Price", "INTEGER");
            EnsureColumn(con, "WbFbsOrders", "FinalPrice", "INTEGER");
            EnsureColumn(con, "WbFbsOrders", "CurrencyCode", "INTEGER");

            EnsureColumn(con, "WbFbsOrders", "ConvertedPrice", "INTEGER");
            EnsureColumn(con, "WbFbsOrders", "ConvertedFinalPrice", "INTEGER");
            EnsureColumn(con, "WbFbsOrders", "ConvertedCurrencyCode", "INTEGER");

            EnsureColumn(con, "WbFbsOrders", "CargoType", "INTEGER");
            EnsureColumn(con, "WbFbsOrders", "IsZeroOrder", "INTEGER");

            EnsureColumn(con, "WbFbsOrders", "SalePrice", "INTEGER");
            EnsureColumn(con, "WbFbsOrders", "ScanPrice", "INTEGER");

            EnsureColumn(con, "WbFbsOrders", "Ddate", "TEXT");
            EnsureColumn(con, "WbFbsOrders", "SellerDate", "TEXT");
            EnsureColumn(con, "WbFbsOrders", "ColorCode", "TEXT");

            EnsureColumn(con, "WbFbsOrders", "AddressFull", "TEXT");
            EnsureColumn(con, "WbFbsOrders", "AddressLatitude", "NUMERIC");
            EnsureColumn(con, "WbFbsOrders", "AddressLongitude", "NUMERIC");

            EnsureColumn(con, "WbFbsOrders", "OfficesJson", "TEXT");
            EnsureColumn(con, "WbFbsOrders", "SkusJson", "TEXT");

            EnsureColumn(con, "WbFbsOrders", "RequiredMetaJson", "TEXT");
            EnsureColumn(con, "WbFbsOrders", "OptionalMetaJson", "TEXT");
            EnsureColumn(con, "WbFbsOrders", "IsB2B", "INTEGER");

            EnsureColumn(con, "WbFbsOrders", "DeliveryType", "TEXT");
            EnsureColumn(con, "WbFbsOrders", "Comment", "TEXT");

            EnsureColumn(con, "WbFbsOrders", "RawJson", "TEXT");
            EnsureColumn(con, "WbFbsOrders", "ImportedAtUtc", "TEXT");

            FixTextNullToEmpty(con, "WbFbsOrders", "Status");
            FixTextNullToEmpty(con, "WbFbsOrders", "SupplierStatus");
            FixTextNullToEmpty(con, "WbFbsOrders", "WbStatus");
            FixTextNullToEmpty(con, "WbFbsOrders", "AddressFull");
            FixTextNullToEmpty(con, "WbFbsOrders", "RawJson");

            // ----------------------------
            // WB Prices & Discounts (PricesAndDiscounts)
            // ----------------------------
            EnsureTable(con, "WbPriceGoods", @"
CREATE TABLE IF NOT EXISTS ""WbPriceGoods"" (
    ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_WbPriceGoods"" PRIMARY KEY AUTOINCREMENT,
    ""NmId"" INTEGER NOT NULL,
    ""VendorCode"" TEXT NOT NULL DEFAULT '',
    ""CurrencyIsoCode4217"" INTEGER NULL,
    ""Discount"" INTEGER NULL,
    ""ClubDiscount"" INTEGER NULL,
    ""EditableSizePrice"" INTEGER NULL,
    ""IsBadTurnover"" INTEGER NULL,
    ""ImportedAtUtc"" TEXT NOT NULL,
    ""RawJson"" TEXT NOT NULL DEFAULT ''
);");

            EnsureTable(con, "WbPriceSizes", @"
CREATE TABLE IF NOT EXISTS ""WbPriceSizes"" (
    ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_WbPriceSizes"" PRIMARY KEY AUTOINCREMENT,
    ""NmId"" INTEGER NOT NULL,
    ""SizeId"" INTEGER NOT NULL,
    ""TechSizeName"" TEXT NOT NULL DEFAULT '',
    ""Price"" NUMERIC NULL,
    ""DiscountedPrice"" NUMERIC NULL,
    ""ClubDiscountedPrice"" NUMERIC NULL,
    ""ImportedAtUtc"" TEXT NOT NULL,
    ""RawJson"" TEXT NOT NULL DEFAULT ''
);");

            EnsureTable(con, "WbPriceQuarantineGoods", @"
CREATE TABLE IF NOT EXISTS ""WbPriceQuarantineGoods"" (
    ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_WbPriceQuarantineGoods"" PRIMARY KEY AUTOINCREMENT,
    ""NmId"" INTEGER NOT NULL,
    ""VendorCode"" TEXT NOT NULL DEFAULT '',
    ""Reason"" TEXT NOT NULL DEFAULT '',
    ""ImportedAtUtc"" TEXT NOT NULL,
    ""RawJson"" TEXT NOT NULL DEFAULT ''
);");

            // Индексы для ускорения выборок / upsert-логики
            ExecNonQuery(con, @"CREATE UNIQUE INDEX IF NOT EXISTS IX_WbPriceGoods_NmId ON WbPriceGoods(NmId);");
            ExecNonQuery(con, @"CREATE UNIQUE INDEX IF NOT EXISTS IX_WbPriceSizes_NmId_SizeId ON WbPriceSizes(NmId, SizeId);");
            ExecNonQuery(con, @"CREATE INDEX IF NOT EXISTS IX_WbPriceSizes_NmId ON WbPriceSizes(NmId);");

            // Подстрахуемся от NULL в обязательных текстовых полях
            FixTextNullToEmpty(con, "WbPriceGoods", "VendorCode");
            FixTextNullToEmpty(con, "WbPriceGoods", "RawJson");
            FixTextNullToEmpty(con, "WbPriceSizes", "TechSizeName");
            FixTextNullToEmpty(con, "WbPriceSizes", "RawJson");
            FixTextNullToEmpty(con, "WbPriceQuarantineGoods", "VendorCode");
            FixTextNullToEmpty(con, "WbPriceQuarantineGoods", "Reason");
            FixTextNullToEmpty(con, "WbPriceQuarantineGoods", "RawJson");

            EnsureColumn(con, "SyncStates", "Key", "TEXT");
            EnsureColumn(con, "SyncStates", "LastSyncUtc", "TEXT");
            EnsureColumn(con, "SyncStates", "LastValueText", "TEXT");
            FixTextNullToEmpty(con, "SyncStates", "Key");
            ExecNonQuery(con, @"CREATE UNIQUE INDEX IF NOT EXISTS IX_SyncStates_Key ON SyncStates(Key);");

            // --- WbRealizationLines ---
            EnsureColumn(con, "WbRealizationLines", "RawJson", "TEXT");
            EnsureColumn(con, "WbRealizationLines", "OrderDt", "TEXT");
            EnsureColumn(con, "WbRealizationLines", "SaleDt", "TEXT");
            FixTextNullToEmpty(con, "WbRealizationLines", "RawJson");

            // Строки, которые часто появляются позже и дают NULL
            EnsureColumn(con, "WbRealizationLines", "TechSize", "TEXT");
            EnsureColumn(con, "WbRealizationLines", "Barcode", "TEXT");
            EnsureColumn(con, "WbRealizationLines", "SupplierArticle", "TEXT");
            EnsureColumn(con, "WbRealizationLines", "WarehouseName", "TEXT");
            EnsureColumn(con, "WbRealizationLines", "Srid", "TEXT");
            EnsureColumn(con, "WbRealizationLines", "DocTypeName", "TEXT");
            EnsureColumn(con, "WbRealizationLines", "SupplierOperName", "TEXT");

            FixTextNullToEmpty(con, "WbRealizationLines", "TechSize");
            FixTextNullToEmpty(con, "WbRealizationLines", "Barcode");
            FixTextNullToEmpty(con, "WbRealizationLines", "SupplierArticle");
            FixTextNullToEmpty(con, "WbRealizationLines", "WarehouseName");
            FixTextNullToEmpty(con, "WbRealizationLines", "Srid");
            FixTextNullToEmpty(con, "WbRealizationLines", "DocTypeName");
            FixTextNullToEmpty(con, "WbRealizationLines", "SupplierOperName");

            // Числа (NULL -> 0, чтобы не падали расчёты)
            EnsureColumn(con, "WbRealizationLines", "Quantity", "INTEGER");
            EnsureColumn(con, "WbRealizationLines", "PriceWithDiscRub", "NUMERIC");
            EnsureColumn(con, "WbRealizationLines", "PpvzForPay", "NUMERIC");
            EnsureColumn(con, "WbRealizationLines", "PpvzSalesCommission", "NUMERIC");
            EnsureColumn(con, "WbRealizationLines", "DeliveryRub", "NUMERIC");
            EnsureColumn(con, "WbRealizationLines", "StorageFee", "NUMERIC");
            EnsureColumn(con, "WbRealizationLines", "Deduction", "NUMERIC");
            EnsureColumn(con, "WbRealizationLines", "Penalty", "NUMERIC");

            FixNumberNullOrEmptyToZero(con, "WbRealizationLines", "Quantity");
            FixNumberNullOrEmptyToZero(con, "WbRealizationLines", "PriceWithDiscRub");
            FixNumberNullOrEmptyToZero(con, "WbRealizationLines", "PpvzForPay");
            FixNumberNullOrEmptyToZero(con, "WbRealizationLines", "PpvzSalesCommission");
            FixNumberNullOrEmptyToZero(con, "WbRealizationLines", "DeliveryRub");
            FixNumberNullOrEmptyToZero(con, "WbRealizationLines", "StorageFee");
            FixNumberNullOrEmptyToZero(con, "WbRealizationLines", "Deduction");
            FixNumberNullOrEmptyToZero(con, "WbRealizationLines", "Penalty");

            // --- WbStockSnapshots ---
            EnsureColumn(con, "WbStockSnapshots", "RawJson", "TEXT");
            EnsureColumn(con, "WbStockSnapshots", "SupplierArticle", "TEXT");
            EnsureColumn(con, "WbStockSnapshots", "TechSize", "TEXT");
            EnsureColumn(con, "WbStockSnapshots", "Subject", "TEXT");
            EnsureColumn(con, "WbStockSnapshots", "Category", "TEXT");
            EnsureColumn(con, "WbStockSnapshots", "Brand", "TEXT");
            EnsureColumn(con, "WbStockSnapshots", "Price", "NUMERIC");
            EnsureColumn(con, "WbStockSnapshots", "Discount", "INTEGER");
            EnsureColumn(con, "WbStockSnapshots", "QuantityFull", "INTEGER");
            EnsureColumn(con, "WbStockSnapshots", "InWayToClient", "INTEGER");
            EnsureColumn(con, "WbStockSnapshots", "InWayFromClient", "INTEGER");
            EnsureColumn(con, "WbStockSnapshots", "IsSupply", "INTEGER");
            EnsureColumn(con, "WbStockSnapshots", "IsRealization", "INTEGER");
            EnsureColumn(con, "WbStockSnapshots", "Code", "TEXT");
            EnsureColumn(con, "WbStockSnapshots", "SCCode", "TEXT");
            EnsureColumn(con, "WbStockSnapshots", "LastChangeDate", "TEXT");

            // иногда “внезапно” появляется в моделях — пусть будет, чтобы EF не падал
            EnsureColumn(con, "WbStockSnapshots", "AddressFull", "TEXT"); // безопасно даже если не используется

            FixTextNullToEmpty(con, "WbStockSnapshots", "RawJson");
            FixTextNullToEmpty(con, "WbStockSnapshots", "AddressFull");

            // Нормализация NULL-чисел
            ExecNonQuery(con, @"
UPDATE WbStockSnapshots SET
    Quantity = COALESCE(Quantity, 0),
    QuantityFull = COALESCE(QuantityFull, Quantity),
    Discount = COALESCE(Discount, 0),
    InWayToClient = COALESCE(InWayToClient, 0),
    InWayFromClient = COALESCE(InWayFromClient, 0)
WHERE
    Quantity IS NULL OR QuantityFull IS NULL OR Discount IS NULL OR InWayToClient IS NULL OR InWayFromClient IS NULL;");

            // ----------------------------
            // Индексы (безопасно)
            // ----------------------------
            ExecNonQuery(con, @"CREATE UNIQUE INDEX IF NOT EXISTS IX_WbFbsOrders_OrderId ON WbFbsOrders(OrderId);");
            ExecNonQuery(con, @"CREATE UNIQUE INDEX IF NOT EXISTS IX_WbProducts_NmId ON WbProducts(NmId);");

            tx.Commit();
        }

        // ---------- FIXERS ----------
        private static void FixNumberNullOrEmptyToZero(SqliteConnection con, string table, string column)
        {
            ExecNonQuery(con, $@"
UPDATE {QuoteIdent(table)}
SET {QuoteIdent(column)} = 0
WHERE {QuoteIdent(column)} IS NULL
   OR TRIM(CAST({QuoteIdent(column)} AS TEXT)) = '';");
        }

        private static void FixTextNullToEmpty(SqliteConnection con, string table, string column)
        {
            ExecNonQuery(con, $@"
UPDATE {QuoteIdent(table)}
SET {QuoteIdent(column)} = ''
WHERE {QuoteIdent(column)} IS NULL;");
        }

        private static void ExecNonQuery(SqliteConnection con, string sql)
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        // ---------- SCHEMA HELPERS ----------
        private static bool ColumnExists(SqliteConnection con, string table, string column)
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info('{EscapeSqlLiteral(table)}');";

            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var name = r.GetString(1);
                if (string.Equals(name, column, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static void EnsureColumn(SqliteConnection con, string table, string column, string sqlType)
        {
            if (!TableExists(con, table)) return;
            if (ColumnExists(con, table, column)) return;

            using var cmd = con.CreateCommand();
            cmd.CommandText = $"ALTER TABLE {QuoteIdent(table)} ADD COLUMN {QuoteIdent(column)} {sqlType};";
            cmd.ExecuteNonQuery();
        }

        private static bool TableExists(SqliteConnection con, string table)
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name LIMIT 1;";
            cmd.Parameters.AddWithValue("$name", table);
            return cmd.ExecuteScalar() != null;
        }

        private static void EnsureTable(SqliteConnection con, string table, string createSql)
        {
            if (TableExists(con, table)) return;
            using var cmd = con.CreateCommand();
            cmd.CommandText = createSql;
            cmd.ExecuteNonQuery();
        }

        private static string QuoteIdent(string ident) =>
            "\"" + (ident ?? "").Replace("\"", "\"\"") + "\"";

        private static string EscapeSqlLiteral(string s) =>
            (s ?? "").Replace("'", "''");
    }
}

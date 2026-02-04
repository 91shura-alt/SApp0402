using Microsoft.EntityFrameworkCore;
using SellerOps.App.Domain;
using System;
using System.IO;

namespace SellerOps.App.Data
{
    public sealed partial class AppDbContext : DbContext
    {
        public static AppDbContext Instance { get; } = new AppDbContext();

        private static readonly object _initLock = new();
        private static bool _isReady;

        private static string? _dbPath;

        /// <summary>
        /// Стабильный путь к БД (не зависит от bin/Debug), чтобы токены не "пропадали" между сборками.
        /// Если раньше БД была рядом с exe — один раз копируем её в LocalAppData.
        /// </summary>
        public static string DbPath
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(_dbPath))
                    return _dbPath;

                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SellerOps");

                Directory.CreateDirectory(dir);

                var newPath = Path.Combine(dir, "sellerops.db");
                var oldPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sellerops.db");

                // Мягкая миграция старого расположения БД -> новое (без потери токенов)
                if (!File.Exists(newPath) && File.Exists(oldPath))
                {
                    File.Copy(oldPath, newPath);
                }

                _dbPath = newPath;
                return _dbPath;
            }
        }

        // ===== DbSets =====
        public DbSet<Supply> Supplies { get; set; } = null!;
        public DbSet<SupplyItem> SupplyItems { get; set; } = null!;
        public DbSet<ScanLog> ScanLogs { get; set; } = null!;
        public DbSet<ApiToken> ApiTokens { get; set; } = null!;

        public DbSet<SupplyPosition> SupplyPositions { get; set; } = null!;

        public DbSet<WbProduct> WbProducts { get; set; } = null!;
        public DbSet<WbRealizationLine> WbRealizationLines { get; set; } = null!;
        public DbSet<WbImportLog> WbImportLogs { get; set; } = null!;
        public DbSet<WbRawFile> WbRawFiles { get; set; } = null!;
        public DbSet<WbStockSnapshot> WbStockSnapshots { get; set; } = null!;
        public DbSet<WbBalance> WbBalances { get; set; } = null!;
        public DbSet<WbFbsOrder> WbFbsOrders { get; set; } = null!;

        // WB Prices & Discounts API (PricesAndDiscounts)
        public DbSet<WbPriceGood> WbPriceGoods { get; set; } = null!;
        public DbSet<WbPriceSize> WbPriceSizes { get; set; } = null!;
        public DbSet<WbPriceQuarantineGood> WbPriceQuarantineGoods { get; set; } = null!;


        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            optionsBuilder.UseSqlite($"Data Source={DbPath}");
        }

        /// <summary>
        /// Инициализация БД: создаём при необходимости и делаем апгрейд схемы.
        /// ВАЖНО: больше не удаляем БД автоматически.
        /// </summary>
        public void EnsureUpToDate()
        {
            lock (_initLock)
            {
                if (_isReady) return;

                Database.EnsureCreated();
                EnsureUpgrade(); // метод в AppDbContext.Upgrade.cs

                _isReady = true;
            }
        }
    }
}

using Microsoft.EntityFrameworkCore;
using SellerOps.App.Domain;
using SellerOps.App.Services;
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

                var configuredPath = AppSettings.Instance.DatabasePath;
                if (!string.IsNullOrWhiteSpace(configuredPath))
                {
                    var fullPath = Path.GetFullPath(configuredPath);
                    if (Directory.Exists(fullPath) || string.IsNullOrWhiteSpace(Path.GetExtension(fullPath)))
                        fullPath = Path.Combine(fullPath, "sellerops.db");
                    var dir = Path.GetDirectoryName(fullPath);
                    if (!string.IsNullOrWhiteSpace(dir))
                        Directory.CreateDirectory(dir);

                    _dbPath = fullPath;
                    return _dbPath;
                }

                var defaultPath = AppSettings.DefaultDatabasePath;
                var defaultDir = Path.GetDirectoryName(defaultPath);
                if (!string.IsNullOrWhiteSpace(defaultDir))
                    Directory.CreateDirectory(defaultDir);

                var oldPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sellerops.db");

                // Мягкая миграция старого расположения БД -> новое (без потери токенов)
                if (!File.Exists(defaultPath) && File.Exists(oldPath))
                {
                    File.Copy(oldPath, defaultPath);
                }

                _dbPath = defaultPath;
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
        public DbSet<SyncState> SyncStates { get; set; } = null!;
        public DbSet<WbRawFile> WbRawFiles { get; set; } = null!;
        public DbSet<WbStockSnapshot> WbStockSnapshots { get; set; } = null!;
        public DbSet<WbBalance> WbBalances { get; set; } = null!;
        public DbSet<WbFbsOrder> WbFbsOrders { get; set; } = null!;

        // WB Prices & Discounts API (PricesAndDiscounts)
        public DbSet<WbPriceGood> WbPriceGoods { get; set; } = null!;
        public DbSet<WbPriceSize> WbPriceSizes { get; set; } = null!;
        public DbSet<WbPriceQuarantineGood> WbPriceQuarantineGoods { get; set; } = null!;

        // WB Promotion API (акции)
        public DbSet<WbPromotionItem> WbPromotionItems { get; set; } = null!;


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

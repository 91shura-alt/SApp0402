using System;
using System.IO;
using System.Text.Json;

namespace SellerOps.App.Services
{
    public sealed class AppSettings
    {
        public string? DatabasePath { get; set; }
        public bool StoreTokensAsPlainText { get; set; }

        public string? EncryptedStatisticsToken { get; set; }
        public bool StatisticsIsSandbox { get; set; }

        public string? EncryptedAnalyticsToken { get; set; }
        public bool AnalyticsIsSandbox { get; set; }

        public string? EncryptedContentToken { get; set; }
        public bool ContentIsSandbox { get; set; }

        public string? EncryptedMarketplaceToken { get; set; }
        public bool MarketplaceIsSandbox { get; set; }

        public int DefaultPeriodDays { get; set; } = 7;

        public static AppSettings Instance { get; } = Load();

        public static string DefaultDatabasePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SellerOps", "sellerops.db");

        private static string SettingsPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SellerOps", "settings.json");

        private static AppSettings Load()
        {
            try
            {
                if (!File.Exists(SettingsPath))
                    return new AppSettings();

                var json = File.ReadAllText(SettingsPath);
                var parsed = JsonSerializer.Deserialize<AppSettings>(json);
                return parsed ?? new AppSettings();
            }
            catch
            {
                return new AppSettings();
            }
        }

        public void Save()
        {
            var dir = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            File.WriteAllText(SettingsPath, json);
        }
    }
}

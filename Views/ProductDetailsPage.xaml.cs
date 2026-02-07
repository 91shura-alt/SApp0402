using Microsoft.EntityFrameworkCore;
using SellerOps.App.Data;
using SellerOps.App.Domain;
using SellerOps.App.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace SellerOps.App.Views
{
    public partial class ProductDetailsPage : Page
    {
        private readonly long _nmId;

        public ProductDetailsPage(long nmId)
        {
            InitializeComponent();
            _nmId = nmId;

            Loaded += async (_, __) => await LoadAsync();
        }

        private sealed class CharRow
        {
            public string Name { get; set; } = "";
            public string Value { get; set; } = "";
        }

        private sealed class PriceRow
        {
            public string Size { get; set; } = "";
            public string Price { get; set; } = "";
            public string DiscountedPrice { get; set; } = "";
            public string ClubPrice { get; set; } = "";
            public string SellerDiscount { get; set; } = "";
            public string ClubDiscount { get; set; } = "";
        }

        private sealed class PromoRow
        {
            public string Name { get; set; } = "";
            public string RequiredDiscount { get; set; } = "";
            public string Status { get; set; } = "";
            public string Details { get; set; } = "";
        }

        private async Task LoadAsync()
        {
            try
            {
                TitleBlock.Text = $"Карточка {_nmId}";
                NmIdBox.Text = _nmId.ToString();

                // 1) Пробуем WB details
                WbCatalogService.WbCardDetails dto;
                try
                {
                    var svc = new WbCatalogService();
                    dto = await svc.GetCardDetailsAsync(_nmId);
                }
                catch (Exception ex)
                {
                    // Не ломаем UI — пойдём в локальную БД
                    dto = new WbCatalogService.WbCardDetails
                    {
                        NmId = _nmId,
                        Title = "",
                        Brand = "",
                        Subject = "",
                        VendorCode = "",
                        Description = "",
                        IsArchived = false,
                        Barcodes = new List<string>(),
                        Characteristics = new List<WbCatalogService.WbCardDetails.CharDto>(),
                        RawJson = $"Ошибка получения с WB: {ex.Message}"
                    };
                }

                // 2) Fallback из локальной таблицы WbProducts
                var p = await AppDbContext.Instance.WbProducts.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.NmId == _nmId);

                if (p != null)
                {
                    if (string.IsNullOrWhiteSpace(dto.Title)) dto.Title = p.Title;
                    if (string.IsNullOrWhiteSpace(dto.Brand)) dto.Brand = p.Brand;
                    if (string.IsNullOrWhiteSpace(dto.Subject)) dto.Subject = p.Subject;
                    if (string.IsNullOrWhiteSpace(dto.VendorCode)) dto.VendorCode = p.VendorCode;
                    if (string.IsNullOrWhiteSpace(dto.Description))
                        dto.Description = TryExtractDescriptionFromRawJson(p.RawJson);

                    // если WB не дал баркоды — пробуем BarcodesJson
                    if ((dto.Barcodes == null || dto.Barcodes.Count == 0) && !string.IsNullOrWhiteSpace(p.BarcodesJson))
                    {
                        try { dto.Barcodes = JsonSerializer.Deserialize<List<string>>(p.BarcodesJson) ?? new List<string>(); }
                        catch { dto.Barcodes = new List<string>(); }
                    }

                    // если размеров нет — берём из локальных полей
                    dto.LengthCm ??= p.LengthCm;
                    dto.WidthCm ??= p.WidthCm;
                    dto.HeightCm ??= p.HeightCm;
                    dto.WeightKg ??= p.WeightKg;

                    // RawJson: если WB пусто — покажем локальный
                    if (string.IsNullOrWhiteSpace(dto.RawJson) && !string.IsNullOrWhiteSpace(p.RawJson))
                        dto.RawJson = p.RawJson;

                    // если archived не заполнен в dto — попробуем из локального
                    // (у тебя bool, так что просто используем p)
                    dto.IsArchived = dto.IsArchived || p.IsArchived;
                }

                // 3) Заполняем UI
                TitleBlock.Text = string.IsNullOrWhiteSpace(dto.Title)
                    ? $"Карточка {_nmId}"
                    : dto.Title;

                BrandBox.Text = dto.Brand ?? "";
                SubjectBox.Text = dto.Subject ?? "";
                VendorCodeBox.Text = dto.VendorCode ?? "";

                var barcodes = dto.Barcodes ?? new List<string>();
                BarcodesBox.Text = barcodes.Count > 0 ? string.Join(", ", barcodes) : "";

                ArchivedBox.Text = dto.IsArchived ? "Да" : "Нет";

                // размеры
                if (dto.LengthCm.HasValue && dto.WidthCm.HasValue && dto.HeightCm.HasValue)
                    DimsBox.Text = $"{dto.LengthCm.Value:0.##} × {dto.WidthCm.Value:0.##} × {dto.HeightCm.Value:0.##}";
                else
                    DimsBox.Text = "";

                // вес
                WeightBox.Text = dto.WeightKg.HasValue ? $"{dto.WeightKg.Value:0.###}" : "";

                // объём
                if (dto.LengthCm.HasValue && dto.WidthCm.HasValue && dto.HeightCm.HasValue)
                {
                    var v = (dto.LengthCm.Value * dto.WidthCm.Value * dto.HeightCm.Value) / 1_000_000.0;
                    VolumeBox.Text = v.ToString("0.######");
                }
                else
                {
                    VolumeBox.Text = "";
                }

                DescBox.Text = dto.Description ?? "";
                RawJsonBox.Text = dto.RawJson ?? "";

                // характеристики
                var rows = (dto.Characteristics ?? new List<WbCatalogService.WbCardDetails.CharDto>())
                    .Select(x => new CharRow
                    {
                        Name = x.Name ?? "",
                        Value = x.Values == null ? "" : string.Join(", ", x.Values.Where(s => !string.IsNullOrWhiteSpace(s)))
                    })
                    .Where(x => !string.IsNullOrWhiteSpace(x.Name) || !string.IsNullOrWhiteSpace(x.Value))
                    .ToList();

                CharGrid.ItemsSource = rows;

                await LoadPricesAsync();
                await LoadPromotionsAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Ошибка загрузки карточки", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task LoadPricesAsync()
        {
            var db = AppDbContext.Instance;

            var goods = await db.WbPriceGoods.AsNoTracking()
                .Where(x => x.NmId == _nmId)
                .OrderByDescending(x => x.ImportedAtUtc)
                .FirstOrDefaultAsync();

            DateTime? latestSizeTs = await db.WbPriceSizes.AsNoTracking()
                .Where(x => x.NmId == _nmId)
                .MaxAsync(x => (DateTime?)x.ImportedAtUtc);

            var sizes = latestSizeTs.HasValue
                ? await db.WbPriceSizes.AsNoTracking()
                    .Where(x => x.NmId == _nmId && x.ImportedAtUtc == latestSizeTs.Value)
                    .OrderBy(x => x.TechSizeName)
                    .ToListAsync()
                : new List<WbPriceSize>();

            var sellerDiscount = goods?.Discount?.ToString() ?? "";
            var clubDiscount = goods?.ClubDiscount?.ToString() ?? "";

            var rows = new List<PriceRow>();

            if (sizes.Count > 0)
            {
                foreach (var size in sizes)
                {
                    rows.Add(new PriceRow
                    {
                        Size = string.IsNullOrWhiteSpace(size.TechSizeName) ? "-" : size.TechSizeName,
                        Price = FormatMoney(size.Price),
                        DiscountedPrice = FormatMoney(size.DiscountedPrice),
                        ClubPrice = FormatMoney(size.ClubDiscountedPrice),
                        SellerDiscount = sellerDiscount,
                        ClubDiscount = clubDiscount
                    });
                }
            }
            else if (goods != null)
            {
                rows.Add(new PriceRow
                {
                    Size = "-",
                    Price = "",
                    DiscountedPrice = "",
                    ClubPrice = "",
                    SellerDiscount = sellerDiscount,
                    ClubDiscount = clubDiscount
                });
            }

            PricesGrid.ItemsSource = rows;
        }

        private async Task LoadPromotionsAsync()
        {
            var rows = new List<PromoRow>();
            var db = AppDbContext.Instance;

            var latestPromotionTs = await db.WbPromotionItems.AsNoTracking()
                .Where(x => x.NmId == _nmId)
                .MaxAsync(x => (DateTime?)x.ImportedAtUtc);

            if (!latestPromotionTs.HasValue || DateTime.UtcNow - latestPromotionTs.Value > TimeSpan.FromHours(6))
            {
                try
                {
                    var svc = new WbPromotionService(db);
                    await svc.RefreshPromotionsForNmIdAsync(_nmId);
                }
                catch (Exception ex)
                {
                    rows.Add(new PromoRow
                    {
                        Name = "Ошибка загрузки",
                        RequiredDiscount = "н/д",
                        Status = "",
                        Details = ex.Message
                    });
                }
            }

            if (rows.Count == 0)
            {
                var promos = await db.WbPromotionItems.AsNoTracking()
                    .Where(x => x.NmId == _nmId)
                    .OrderByDescending(x => x.ImportedAtUtc)
                    .ToListAsync();

                rows.AddRange(promos.Select(x => new PromoRow
                {
                    Name = x.Name,
                    RequiredDiscount = x.RequiredDiscount,
                    Status = x.Status,
                    Details = x.Details
                }));
            }

            if (rows.Count == 0)
            {
                rows.Add(new PromoRow
                {
                    Name = "Нет данных",
                    RequiredDiscount = "н/д",
                    Status = "",
                    Details = "Промо-акции не найдены."
                });
            }

            PromotionsGrid.ItemsSource = rows;
        }

        private static string? TryExtractDescriptionFromRawJson(string? rawJson)
        {
            if (string.IsNullOrWhiteSpace(rawJson))
                return null;

            try
            {
                using var doc = JsonDocument.Parse(rawJson);
                var root = doc.RootElement;

                return TryGetString(root, "description")
                    ?? TryGetString(root, "descriptionRu")
                    ?? TryGetString(root, "descriptionEn");
            }
            catch
            {
                return null;
            }
        }

        private static string FormatMoney(decimal? value)
        {
            return value.HasValue ? value.Value.ToString("0.##") : "";
        }

        private static string? TryGetString(JsonElement e, string name)
        {
            return e.TryGetProperty(name, out var p)
                ? (p.ValueKind == JsonValueKind.String ? p.GetString() : p.ToString())
                : null;
        }

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            if (NavigationService?.CanGoBack == true)
                NavigationService.GoBack();
        }
    }
}

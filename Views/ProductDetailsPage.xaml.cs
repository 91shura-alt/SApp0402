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
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Ошибка загрузки карточки", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            if (NavigationService?.CanGoBack == true)
                NavigationService.GoBack();
        }
    }
}

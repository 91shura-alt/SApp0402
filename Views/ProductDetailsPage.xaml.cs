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
using System.Windows.Media;

namespace SellerOps.App.Views
{
    public partial class ProductDetailsPage : Page
    {
        private readonly long _nmId;
        private int _periodDays = 30;

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

        private sealed class CalendarRow
        {
            public string Name { get; set; } = "";
            public string DateFrom { get; set; } = "";
            public string DateTo { get; set; } = "";
            public string Status { get; set; } = "";
            public string Participation { get; set; } = "";
            public string Details { get; set; } = "";
        }

        private async Task LoadAsync()
        {
            try
            {
                TitleBlock.Text = $"Карточка {_nmId}";
                NmIdValue.Text = _nmId.ToString();

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

                SubjectValue.Text = dto.Subject ?? "";
                VendorCodeValue.Text = dto.VendorCode ?? "";

                var barcodes = dto.Barcodes ?? new List<string>();
                BarcodesValue.Text = barcodes.Count > 0 ? string.Join(", ", barcodes) : "";

                DescBox.Text = dto.Description ?? "";
                RawJsonBox.Text = dto.RawJson ?? "";
                CostValue.Text = p?.Cost.HasValue == true ? p.Cost.Value.ToString("0.##") : "н/д";
                PrepValue.Text = "н/д";
                RatingValue.Text = "н/д";
                ReviewsValue.Text = "н/д";
                LinksValue.Text = $"https://www.wildberries.ru/catalog/{_nmId}/detail.aspx";

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
                await LoadCalendarPromotionsAsync();
                await LoadDashboardStatsAsync();
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

            var firstSize = sizes.FirstOrDefault();
            PriceNoDiscountValue.Text = FormatMoney(firstSize?.Price);
            PriceWithDiscountValue.Text = FormatMoney(firstSize?.DiscountedPrice);
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

        private async Task LoadCalendarPromotionsAsync()
        {
            var rows = new List<CalendarRow>();
            var db = AppDbContext.Instance;

            var latestPromotionTs = await db.WbPromotionCalendarItems.AsNoTracking()
                .Where(x => x.NmId == _nmId)
                .MaxAsync(x => (DateTime?)x.ImportedAtUtc);

            if (!latestPromotionTs.HasValue || DateTime.UtcNow - latestPromotionTs.Value > TimeSpan.FromHours(6))
            {
                try
                {
                    var svc = new WbPromotionService(db);
                    await svc.RefreshCalendarPromotionsForNmIdAsync(_nmId);
                }
                catch (Exception ex)
                {
                    rows.Add(new CalendarRow
                    {
                        Name = "Ошибка загрузки",
                        DateFrom = "н/д",
                        DateTo = "н/д",
                        Status = "",
                        Participation = "",
                        Details = ex.Message
                    });
                }
            }

            if (rows.Count == 0)
            {
                var promos = await db.WbPromotionCalendarItems.AsNoTracking()
                    .Where(x => x.NmId == _nmId)
                    .OrderByDescending(x => x.ImportedAtUtc)
                    .ToListAsync();

                rows.AddRange(promos.Select(x => new CalendarRow
                {
                    Name = x.Name,
                    DateFrom = x.DateFrom,
                    DateTo = x.DateTo,
                    Status = x.Status,
                    Participation = x.Participation,
                    Details = x.Details
                }));
            }

            if (rows.Count == 0)
            {
                rows.Add(new CalendarRow
                {
                    Name = "Нет данных",
                    DateFrom = "н/д",
                    DateTo = "н/д",
                    Status = "",
                    Participation = "",
                    Details = "Календарь акций не вернул данных."
                });
            }

            CalendarPromotionsGrid.ItemsSource = rows;
        }

        private async Task LoadDashboardStatsAsync()
        {
            if (DashboardStatus == null || DashboardRefreshButton == null)
                return;

            DashboardRefreshButton.IsEnabled = false;
            DashboardStatus.Text = "Загрузка дашборда...";

            try
            {
                var db = AppDbContext.Instance;
                var to = DateTime.UtcNow;
                var from = to.AddDays(-_periodDays);

                var realizations = await db.WbRealizationLines.AsNoTracking()
                    .Where(x => x.NmId == _nmId && x.RrDt.HasValue && x.RrDt.Value >= from && x.RrDt.Value <= to)
                    .ToListAsync();

                var salesLines = realizations.Where(x => !IsReturn(x)).ToList();
                var returnLines = realizations.Where(IsReturn).ToList();

                var ordersQty = realizations.Sum(x => x.Quantity);
                var salesQty = salesLines.Sum(x => x.Quantity);
                var returnQty = returnLines.Sum(x => x.Quantity);
                OrdersValue.Text = ordersQty.ToString();
                SalesValue.Text = salesQty.ToString();
                ReturnsValue.Text = returnQty.ToString();

                var buyout = ordersQty > 0 ? (decimal)salesQty / ordersQty * 100m : 0m;
                BuyoutValue.Text = ordersQty > 0 ? $"{buyout:0.##}%" : "н/д";

                var salesDays = salesLines
                    .Where(x => x.SaleDt.HasValue || x.RrDt.HasValue)
                    .Select(x => (x.SaleDt ?? x.RrDt)!.Value.Date)
                    .Distinct()
                    .Count();
                SalesDaysValue.Text = salesDays.ToString();

                var revenue = salesLines.Sum(x => x.PriceWithDiscRub);
                RevenueValue.Text = revenue.ToString("0.##");

                var expenses = realizations.Sum(x => x.PpvzSalesCommission + x.DeliveryRub + x.StorageFee + x.Deduction + x.Penalty);
                ExpensesValue.Text = expenses.ToString("0.##");

                var profit = revenue - expenses;
                ProfitValue.Text = profit.ToString("0.##");

                var margin = revenue > 0 ? profit / revenue * 100m : 0m;
                MarginValue.Text = revenue > 0 ? $"{margin:0.##}%" : "н/д";

                var cost = await db.WbProducts.AsNoTracking()
                    .Where(x => x.NmId == _nmId)
                    .Select(x => x.Cost)
                    .FirstOrDefaultAsync();
                var totalCost = cost.HasValue ? cost.Value * salesQty : 0m;
                var roi = totalCost > 0 ? profit / totalCost * 100m : 0m;
                RoiValue.Text = totalCost > 0 ? $"{roi:0.##}%" : "н/д";

                ExpensesBlock.Text = expenses > 0 ? $"Комиссия+логистика: {expenses:0.##}" : "Нет данных";

                var latestStockDate = await db.WbStockSnapshots.AsNoTracking()
                    .Where(x => x.NmId == _nmId)
                    .MaxAsync(x => (DateTime?)x.SnapshotAt);

                if (latestStockDate.HasValue)
                {
                    var stocks = await db.WbStockSnapshots.AsNoTracking()
                        .Where(x => x.NmId == _nmId && x.SnapshotAt == latestStockDate.Value)
                        .OrderByDescending(x => x.Quantity)
                        .ToListAsync();

                    WarehousesBlock.Text = stocks.Count == 0
                        ? "Нет данных"
                        : string.Join(Environment.NewLine, stocks.Take(5).Select(x => $"{x.Warehouse}: {x.Quantity}"));
                }
                else
                {
                    WarehousesBlock.Text = "Нет данных";
                }

                PhotosBlock.Text = "Нет данных";

                var revenueSeries = salesLines
                    .Where(x => x.RrDt.HasValue)
                    .GroupBy(x => x.RrDt!.Value.Date)
                    .OrderBy(x => x.Key)
                    .Select(x => x.Sum(v => v.PriceWithDiscRub))
                    .ToList();

                var ordersSeries = salesLines
                    .Where(x => x.RrDt.HasValue)
                    .GroupBy(x => x.RrDt!.Value.Date)
                    .OrderBy(x => x.Key)
                    .Select(x => (decimal)x.Sum(v => v.Quantity))
                    .ToList();

                var stockSeries = await db.WbStockSnapshots.AsNoTracking()
                    .Where(x => x.NmId == _nmId && x.SnapshotAt >= from && x.SnapshotAt <= to)
                    .GroupBy(x => x.SnapshotAt.Date)
                    .OrderBy(x => x.Key)
                    .Select(x => (decimal)x.Sum(v => v.Quantity))
                    .ToListAsync();

                var salesSeries = salesLines
                    .Where(x => x.RrDt.HasValue)
                    .GroupBy(x => x.RrDt!.Value.Date)
                    .OrderBy(x => x.Key)
                    .Select(x => (decimal)x.Sum(v => v.Quantity))
                    .ToList();

                SetChart(RevenueChartCanvas, RevenueLine, OrdersLine, revenueSeries, ordersSeries);
                SetChart(StockChartCanvas, StocksLine, SalesLine, stockSeries, salesSeries);
                TrendValue.Text = BuildTrendText(revenueSeries);

                DashboardStatus.Text = "Данные дашборда обновлены.";
            }
            catch (Exception ex)
            {
                DashboardStatus.Text = $"Ошибка дашборда: {ex.Message}";
            }
            finally
            {
                DashboardRefreshButton.IsEnabled = true;
            }
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

        private async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            await LoadAsync();
        }

        private async void DashboardRefresh_Click(object sender, RoutedEventArgs e)
        {
            await LoadDashboardStatsAsync();
        }

        private async void PeriodCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PeriodCombo.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out var days))
            {
                _periodDays = days > 0 ? days : 30;
                await LoadAsync();
            }
        }

        private static bool IsReturn(WbRealizationLine line)
        {
            return (line.SupplierOperName?.IndexOf("возврат", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0
                || (line.DocTypeName?.IndexOf("возврат", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0;
        }

        private static void SetChart(Canvas canvas, Polyline line1, Polyline line2, List<decimal> series1, List<decimal> series2)
        {
            var width = canvas.ActualWidth > 0 ? canvas.ActualWidth : 600;
            var height = canvas.ActualHeight > 0 ? canvas.ActualHeight : 180;

            line1.Points = BuildPoints(series1, width, height);
            line2.Points = BuildPoints(series2, width, height);
        }

        private static PointCollection BuildPoints(List<decimal> values, double width, double height)
        {
            var points = new PointCollection();
            if (values.Count == 0)
                return points;

            var max = values.Max();
            var min = values.Min();
            var range = max - min;
            if (range == 0)
                range = 1;

            var step = values.Count > 1 ? width / (values.Count - 1) : width;
            for (int i = 0; i < values.Count; i++)
            {
                var x = i * step;
                var normalized = (values[i] - min) / range;
                var y = height - (double)(normalized * (decimal)height);
                points.Add(new System.Windows.Point(x, y));
            }

            return points;
        }

        private static string BuildTrendText(List<decimal> series)
        {
            if (series.Count < 4)
                return "н/д";

            var half = series.Count / 2;
            var first = series.Take(half).Sum();
            var second = series.Skip(half).Sum();
            if (first == 0)
                return "н/д";

            var delta = (second - first) / first * 100m;
            return delta >= 0 ? $"↑ {delta:0.##}%" : $"↓ {Math.Abs(delta):0.##}%";
        }
    }
}

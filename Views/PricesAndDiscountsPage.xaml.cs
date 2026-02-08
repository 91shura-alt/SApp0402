using Microsoft.EntityFrameworkCore;
using SellerOps.App.Data;
using SellerOps.App.Domain;
using SellerOps.App.Services;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace SellerOps.App.Views
{
    public partial class PricesAndDiscountsPage : Page
    {
        private readonly AppDbContext _db = AppDbContext.Instance;
        private readonly WbPricesAndDiscountsService _svc = new WbPricesAndDiscountsService(AppDbContext.Instance);
        private readonly WbPromotionService _promoSvc = new WbPromotionService(AppDbContext.Instance);

        private CancellationTokenSource? _cts;
        private string _search = "";
        private long? _selectedNmId;

        public PricesAndDiscountsPage()
        {
            InitializeComponent();
            _ = ReloadGoodsAsync();
        }

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            NavigationService?.GoBack();
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            if (_cts != null)
            {
                _cts.Cancel();
                return;
            }

            _cts = new CancellationTokenSource();
            try
            {
                SetBusy(true, "Загрузка из WB...");

                // на старой БД сначала накатываем upgrade
                _db.EnsureUpgrade();

                var (g, s) = await _svc.ImportAllGoodsAsync(ct: _cts.Token);
                await ReloadGoodsAsync();
                SetBusy(false, $"Готово: товаров {g}, размеров {s}");
            }
            catch (OperationCanceledException)
            {
                SetBusy(false, "Отменено");
            }
            catch (Exception ex)
            {
                SetBusy(false, "Ошибка");
                MessageBox.Show(ex.Message, "PricesAndDiscounts", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _cts.Dispose();
                _cts = null;
            }
        }

        private async void Quarantine_Click(object sender, RoutedEventArgs e)
        {
            if (_cts != null)
            {
                _cts.Cancel();
                return;
            }

            _cts = new CancellationTokenSource();
            try
            {
                SetBusy(true, "Загрузка карантина...");

                _db.EnsureUpgrade();

                var count = await _svc.ImportQuarantineAsync(_cts.Token);
                SetBusy(false, $"Карантин: {count}");
            }
            catch (OperationCanceledException)
            {
                SetBusy(false, "Отменено");
            }
            catch (Exception ex)
            {
                SetBusy(false, "Ошибка");
                MessageBox.Show(ex.Message, "Quarantine", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _cts.Dispose();
                _cts = null;
            }
        }

        private async Task ReloadGoodsAsync()
        {
            var q = _db.WbPriceGoods.AsNoTracking().OrderByDescending(x => x.ImportedAtUtc);

            if (!string.IsNullOrWhiteSpace(_search))
            {
                var s = _search.Trim();
                if (long.TryParse(s, out var nm))
                {
                    q = q.Where(x => x.NmId == nm).OrderByDescending(x => x.ImportedAtUtc);
                }
                else
                {
                    q = q.Where(x => x.VendorCode.Contains(s)).OrderByDescending(x => x.ImportedAtUtc);
                }
            }

            GoodsGrid.ItemsSource = await q.Take(5000).ToListAsync();

            SizesGrid.ItemsSource = null;
        }

        private async Task ReloadSizesAsync(long nmId)
        {
            SizesGrid.ItemsSource = await _db.WbPriceSizes.AsNoTracking()
                .Where(x => x.NmId == nmId)
                .OrderBy(x => x.SizeId)
                .ToListAsync();
        }

        private async void GoodsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (GoodsGrid.SelectedItem is WbPriceGood g)
            {
                _selectedNmId = g.NmId;
                await ReloadSizesAsync(g.NmId);
                await ReloadCalendarAsync(g.NmId, allowRefresh: false);
            }
            else
            {
                _selectedNmId = null;
                SizesGrid.ItemsSource = null;
                CalendarPromotionsGrid.ItemsSource = null;
            }
        }

        private async void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _search = SearchBox.Text ?? "";
            await ReloadGoodsAsync();
        }

        private void SetBusy(bool busy, string status)
        {
            StatusBlock.Text = status;
            RefreshButton.Content = busy ? "Отменить" : "Обновить из WB";
            QuarantineButton.IsEnabled = !busy;
            SearchBox.IsEnabled = !busy;
            CalendarRefreshButton.IsEnabled = !busy;
        }

        private async void CalendarRefresh_Click(object sender, RoutedEventArgs e)
        {
            if (_cts != null)
            {
                _cts.Cancel();
                return;
            }

            if (!_selectedNmId.HasValue)
            {
                MessageBox.Show("Выберите товар, чтобы обновить календарь акций.", "Календарь акций", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _cts = new CancellationTokenSource();
            try
            {
                SetBusy(true, "Загрузка календаря акций...");
                _db.EnsureUpgrade();

                await _promoSvc.RefreshCalendarPromotionsForNmIdAsync(_selectedNmId.Value, _cts.Token);
                await ReloadCalendarAsync(_selectedNmId.Value, allowRefresh: false);
                SetBusy(false, "Календарь акций обновлён");
            }
            catch (OperationCanceledException)
            {
                SetBusy(false, "Отменено");
            }
            catch (Exception ex)
            {
                SetBusy(false, "Ошибка");
                MessageBox.Show(ex.Message, "Календарь акций", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _cts.Dispose();
                _cts = null;
            }
        }

        private async Task ReloadCalendarAsync(long nmId, bool allowRefresh)
        {
            DateTime? latestTs = await _db.WbPromotionCalendarItems.AsNoTracking()
                .Where(x => x.NmId == nmId)
                .MaxAsync(x => (DateTime?)x.ImportedAtUtc);

            if (allowRefresh && (!latestTs.HasValue || DateTime.UtcNow - latestTs.Value > TimeSpan.FromHours(6)))
            {
                await _promoSvc.RefreshCalendarPromotionsForNmIdAsync(nmId);
            }

            CalendarPromotionsGrid.ItemsSource = await _db.WbPromotionCalendarItems.AsNoTracking()
                .Where(x => x.NmId == nmId)
                .OrderByDescending(x => x.ImportedAtUtc)
                .ToListAsync();
        }
    }
}

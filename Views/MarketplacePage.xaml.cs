using SellerOps.App.Data;
using SellerOps.App.Domain;
using SellerOps.App.Services;
using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace SellerOps.App.Views
{
    public partial class MarketplacePage : Page
    {
        private readonly AppDbContext _db = AppDbContext.Instance;
        private readonly WbMarketplaceService _svc = new();
        private CancellationTokenSource? _cts;

        public MarketplacePage()
        {
            InitializeComponent();

            FromDate.SelectedDate = DateTime.Today.AddDays(-7);
            ToDate.SelectedDate = DateTime.Today;

            Loaded += (_, __) => Refresh();
        }

        private void Refresh()
        {
            try
            {
                // FBS: deliveryType обычно "fbs", но иногда поле может быть пустым — учитываем оба варианта
                OrdersGrid.ItemsSource = _db.WbFbsOrders
    .Where(x => x.DeliveryType == null || x.DeliveryType.ToLower() == "fbs")
    .OrderBy(x => x.Status)                // статус
    .ThenBy(x => x.WarehouseId)            // склад
    .ThenByDescending(x => x.CreatedAt)    // свежие выше
    .Take(2000)
    .ToList();
                ApplyOrdersView();

                RawGrid.ItemsSource = _db.WbRawFiles
                    .Where(x => x.Kind.StartsWith("marketplace_fbs_"))
                    .OrderByDescending(x => x.Id)
                    .Take(500)
                    .ToList();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Ошибка обновления", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task RunAsync(string caption, Func<CancellationToken, Task<int>> action)
        {
            if (_cts != null)
            {
                MessageBox.Show("Операция уже выполняется.", "WB Marketplace (FBS)", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                _cts = new CancellationTokenSource();
                BusyBar.Visibility = Visibility.Visible;
                CancelBtn.Visibility = Visibility.Visible;
                BusyText.Text = caption;

                LoadNewFbsBtn.IsEnabled = false;
                LoadPeriodFbsBtn.IsEnabled = false;

                var added = await action(_cts.Token);
                BusyText.Text = $"Готово. Добавлено новых: {added}";
                Refresh();
            }
            catch (OperationCanceledException)
            {
                BusyText.Text = "Отменено.";
            }
            catch (Exception ex)
            {
                BusyText.Text = "Ошибка.";
                MessageBox.Show(ex.Message, "WB Marketplace (FBS)", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BusyBar.Visibility = Visibility.Collapsed;
                CancelBtn.Visibility = Visibility.Collapsed;

                LoadNewFbsBtn.IsEnabled = true;
                LoadPeriodFbsBtn.IsEnabled = true;

                _cts?.Dispose();
                _cts = null;
            }
        }

        private DateTime GetFromUtc()
        {
            var from = FromDate.SelectedDate ?? DateTime.Today.AddDays(-7);
            return DateTime.SpecifyKind(from.Date, DateTimeKind.Local).ToUniversalTime();
        }

        private DateTime GetToUtc()
        {
            var to = ToDate.SelectedDate ?? DateTime.Today;
            var end = to.Date.AddDays(1).AddSeconds(-1);
            return DateTime.SpecifyKind(end, DateTimeKind.Local).ToUniversalTime();
        }

        private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

        private void CancelBtn_Click(object sender, RoutedEventArgs e)
            => _cts?.Cancel();

        private async void LoadNewFbs_Click(object sender, RoutedEventArgs e)
            => await RunAsync("Загружаю новые FBS заказы...", ct => _svc.ImportNewFbsOrdersAsync(ct));

        private async void LoadPeriodFbs_Click(object sender, RoutedEventArgs e)
        {
            var from = GetFromUtc();
            var to = GetToUtc();

            await RunAsync($"Загрузка FBS за период {from:yyyy-MM-dd}..{to:yyyy-MM-dd}...", async ct =>
            {
                var total = 0;
                var cur = from;

                while (cur <= to)
                {
                    ct.ThrowIfCancellationRequested();

                    var chunkTo = cur.AddDays(29);
                    if (chunkTo > to) chunkTo = to;

                    total += await _svc.ImportFbsOrdersByPeriodAsync(cur, chunkTo, 1000, ct);

                    cur = chunkTo.AddSeconds(1);
                }

                return total;
            });
        }
        private void ApplyOrdersView()
        {
            var view = CollectionViewSource.GetDefaultView(OrdersGrid.ItemsSource);
            if (view == null) return;

            view.SortDescriptions.Clear();
            view.SortDescriptions.Add(new SortDescription(nameof(WbFbsOrder.Status), ListSortDirection.Ascending));
            view.SortDescriptions.Add(new SortDescription(nameof(WbFbsOrder.WarehouseId), ListSortDirection.Ascending));
            view.SortDescriptions.Add(new SortDescription(nameof(WbFbsOrder.CreatedAt), ListSortDirection.Descending));

            if (view is ListCollectionView lcv)
            {
                lcv.GroupDescriptions.Clear();
                lcv.GroupDescriptions.Add(new PropertyGroupDescription(nameof(WbFbsOrder.Status)));
                lcv.GroupDescriptions.Add(new PropertyGroupDescription(nameof(WbFbsOrder.WarehouseId)));
            }
        }

        private void OrdersGrid_AutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
        {
            // скрываем тяжелый JSON, чтобы таблица не лагала
            if (e.PropertyName == "RawJson")
                e.Column.Visibility = Visibility.Collapsed;

            // красивый формат дат
            if (e.PropertyName == "ImportedAtUtc" || e.PropertyName == "CreatedAt")
            {
                if (e.Column is DataGridTextColumn tc)
                    tc.Binding.StringFormat = "yyyy-MM-dd HH:mm";
            }
        }

        private void RawGrid_AutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
        {
            // скрываем большой json
            if (e.PropertyName == "Json")
                e.Column.Visibility = Visibility.Collapsed;

            if (e.PropertyName == "CreatedAtUtc")
            {
                if (e.Column is DataGridTextColumn tc)
                    tc.Binding.StringFormat = "yyyy-MM-dd HH:mm";
            }
        }
        private static int StatusKey(string? s)
        {
            s = (s ?? "").Trim().ToLowerInvariant();
            return s switch
            {
                "new" => 0,
                "waiting" => 1,
                "confirm" => 2,
                "inwork" => 3,
                "ready" => 4,
                "completed" => 5,
                "cancel" => 99,
                _ => 50
            };
        }

    }
}

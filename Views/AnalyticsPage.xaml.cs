using Microsoft.EntityFrameworkCore;
using SellerOps.App.Data;
using SellerOps.App.Services;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace SellerOps.App.Views
{
    public partial class AnalyticsPage : Page
    {
        private readonly AppDbContext _db;
        private readonly WbStatisticsService _stat;
        private readonly AnalyticsService _analytics;

        private CancellationTokenSource? _cts;

        public AnalyticsPage()
        {
            InitializeComponent();

            _db = AppDbContext.Instance;
            _stat = new WbStatisticsService(_db);
            _analytics = new AnalyticsService(_db);

            // Дефолтный диапазон дат
            FromDate.SelectedDate ??= DateTime.Today.AddDays(-7);
            ToDate.SelectedDate ??= DateTime.Today;

            RefreshAll();
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
        }

        private async void LoadReal_Click(object sender, RoutedEventArgs e)
        {
            var from = (FromDate.SelectedDate ?? DateTime.Today.AddDays(-7)).Date;
            var to = (ToDate.SelectedDate ?? DateTime.Today).Date;

            await RunBusyAsync("Загружаю реализации WB…", async ct =>
            {
                // reason: "ui" — чтобы в логах было понятно, что это ручной запуск
                var added = await _stat.ImportRealizationByPeriodAsync(from, to, "ui", ct);
                BusyText.Text = $"Готово. Добавлено строк: {added}";
            });

            Tabs.SelectedItem = RealsTab;
            RefreshReals();
        }

        private async void LoadStocks_Click(object sender, RoutedEventArgs e)
        {
            await RunBusyAsync("Снимаю остатки WB…", async ct =>
            {
                var added = await _stat.SnapshotStocksAsync(null, ct);
                BusyText.Text = $"Готово. Добавлено строк: {added}";
            });

            Tabs.SelectedItem = StocksTab;
            RefreshStocks();
        }

        private async void LoadReports_Click(object sender, RoutedEventArgs e)
        {
            var from = (FromDate.SelectedDate ?? DateTime.Today.AddDays(-7)).Date;
            var to = (ToDate.SelectedDate ?? DateTime.Today).Date;

            await RunBusyAsync("Загружаю прочие отчёты WB…", async ct =>
            {
                var saved = await _stat.ImportAdditionalStatisticsReportsByPeriodAsync(from, to, ct);
                BusyText.Text = $"Готово. Строк в прочих отчётах: {saved}";
            });

            Tabs.SelectedItem = ReportsTab;
            RefreshReports();
        }

        private void BuildSummary_Click(object sender, RoutedEventArgs e)
        {
            Tabs.SelectedItem = SummaryTab;
            RefreshSummary();
        }

        // ---------------- UI helpers ----------------

        private async Task RunBusyAsync(string text, Func<CancellationToken, Task> action)
        {
            if (_cts != null) return; // уже что-то выполняется

            _cts = new CancellationTokenSource();
            SetBusy(true, text);

            try
            {
                await action(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                BusyText.Text = "Отменено.";
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false, "");
                _cts.Dispose();
                _cts = null;
            }
        }

        private void SetBusy(bool isBusy, string text)
        {
            BusyBar.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
            CancelBtn.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
            BusyText.Text = text;

            LoadRealBtn.IsEnabled = !isBusy;
            LoadStocksBtn.IsEnabled = !isBusy;
            LoadReportsBtn.IsEnabled = !isBusy;
            BuildSummaryBtn.IsEnabled = !isBusy;

            FromDate.IsEnabled = !isBusy;
            ToDate.IsEnabled = !isBusy;
        }

        // ---------------- Data refresh ----------------

        private void RefreshAll()
        {
            RefreshSummary();
            RefreshReals();
            RefreshStocks();
            RefreshReports();
        }

        private void RefreshSummary()
        {
            var from = (FromDate.SelectedDate ?? DateTime.Today.AddDays(-7)).Date;
            var to = (ToDate.SelectedDate ?? DateTime.Today).Date;

            SummaryGrid.ItemsSource = _analytics.BuildProfitSummary(from, to);
        }

        private void RefreshReals()
        {
            var from = (FromDate.SelectedDate ?? DateTime.Today.AddDays(-7)).Date;
            var to = (ToDate.SelectedDate ?? DateTime.Today).Date.AddDays(1).AddTicks(-1); // конец дня

            RealGrid.ItemsSource = _db.WbRealizationLines.AsNoTracking()
                .Where(x => x.RrDt.HasValue && x.RrDt.Value >= from && x.RrDt.Value <= to)
                .OrderByDescending(x => x.RrDt ?? DateTime.MinValue)
                .ToList();
        }

        private void RefreshStocks()
        {
            var lastSnap = _db.WbStockSnapshots.AsNoTracking()
                .Select(x => x.SnapshotAt)
                .OrderByDescending(x => x)
                .FirstOrDefault();

            if (lastSnap == default)
            {
                StockGrid.ItemsSource = null;
                return;
            }

            StockGrid.ItemsSource = _db.WbStockSnapshots.AsNoTracking()
                .Where(x => x.SnapshotAt == lastSnap)
                .OrderByDescending(x => x.Quantity)
                .ToList();
        }

        private void RefreshReports()
        {
            var from = (FromDate.SelectedDate ?? DateTime.Today.AddDays(-7)).Date;
            var to = (ToDate.SelectedDate ?? DateTime.Today).Date;

            ReportsGrid.ItemsSource = _db.WbImportLogs.AsNoTracking()
                .Where(x => x.Kind.StartsWith("statistics_") && x.Day >= from && x.Day <= to)
                .OrderByDescending(x => x.ImportedAtUtc)
                .ToList();
        }

        // ---------------- Column formatting ----------------

        private void SummaryGrid_AutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
            => FormatAutoColumn(e);

        private void RealGrid_AutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
            => FormatAutoColumn(e);

        private void StockGrid_AutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
            => FormatAutoColumn(e);

        private void ReportsGrid_AutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
            => FormatAutoColumn(e);

        private static void FormatAutoColumn(DataGridAutoGeneratingColumnEventArgs e)
        {
            e.Column.Header = PrettyHeader(e.PropertyName);

            if (e.Column is DataGridTextColumn tc && tc.Binding is Binding b)
            {
                var t = e.PropertyType;

                if (t == typeof(DateTime) || t == typeof(DateTime?))
                    b.StringFormat = "dd.MM.yyyy HH:mm";
                else if (t == typeof(decimal) || t == typeof(decimal?))
                    b.StringFormat = "N2";
                else if (t == typeof(double) || t == typeof(double?))
                    b.StringFormat = "N2";
            }
        }

        private static string PrettyHeader(string name)
        {
            return name switch
            {
                "RrdId" => "RRD ID",
                "RrDt" => "Дата",
                "SupplierArticle" => "Артикул",
                "WarehouseName" => "Склад",
                "TechSize" => "Размер",
                "PriceWithDiscRub" => "Цена со скидкой",
                "PpvzForPay" => "К перечислению",
                "PpvzSalesCommission" => "Комиссия",
                "SnapshotAt" => "Снимок",
                "NmId" => "Номенклатура (NM ID)",
                "Title" => "Наименование",
                "Article" => "Артикул продавца",
                "Qty" => "Количество",
                "RevenueToPay" => "К перечислению",
                "Commission" => "Комиссия",
                "Delivery" => "Логистика",
                "Storage" => "Хранение",
                "Penalties" => "Штрафы",
                "Deductions" => "Удержания",
                "DirectWBFees" => "Прямые расходы WB",
                "Cost" => "Себестоимость",
                "Prep" => "Подготовка",
                "Ads" => "Реклама",
                "NetProfit" => "Чистая прибыль",
                "MarginPct" => "Маржа, %",
                "Id" => "ID",
                "Kind" => "Тип отчёта",
                "Day" => "Дата отчёта",
                "ImportedAtUtc" => "Импортировано (UTC)",
                "IsComplete" => "Завершено",
                "AddedRows" => "Строк",
                "MaxRrdId" => "Макс. RRD ID",
                _ => name
            };
        }
    }
}

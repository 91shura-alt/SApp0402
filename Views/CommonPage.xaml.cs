using SellerOps.App.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace SellerOps.App.Views
{
    public partial class CommonPage : Page
    {
        private readonly WbCommonService _svc = new();

        private string? _sellerRaw;
        private string? _newsRaw;

        private List<NewsRowVm> _newsAll = new();

        public CommonPage()
        {
            InitializeComponent();
        }

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            if (NavigationService?.CanGoBack == true)
                NavigationService.GoBack();
        }

        // ---------------- seller-info ----------------

        private async void RefreshSellerInfo_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                StatusText.Text = "Обновляю seller-info...";

                var (summary, raw) = await _svc.FetchSellerInfoAsync();
                _sellerRaw = raw;

                SellerSummaryBox.Text = summary ?? "";
                SellerRawBox.Text = raw ?? "";
                SellerRawExpander.IsExpanded = false;

                StatusText.Text = "Готово";
            }
            catch (Exception ex)
            {
                StatusText.Text = "Ошибка";
                MessageBox.Show(ex.Message, "WB Common", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ShowSellerRaw_Click(object sender, RoutedEventArgs e)
        {
            SellerRawExpander.IsExpanded = true;
            SellerRawBox.Text = _sellerRaw ?? SellerRawBox.Text;
        }

        // ---------------- news ----------------

        private async void RefreshNews_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                StatusText.Text = "Обновляю новости...";

                var (items, raw) = await _svc.FetchNewsAsync();
                _newsRaw = raw;

                _newsAll = (items ?? new List<WbCommonService.NewsRow>())
                    .Select(x => new NewsRowVm
                    {
                        DtUtc = x.DtUtc ?? x.Dt,
                        Dt = FormatDt(x.DtUtc ?? x.Dt),
                        Title = x.Title ?? "",
                        Body = x.Body ?? ""
                    })
                    .OrderByDescending(x => x.DtUtc ?? DateTime.MinValue)
                    .ToList();

                NewsRawBox.Text = raw ?? "";
                NewsRawExpander.IsExpanded = false;

                ApplyNewsFilter();

                StatusText.Text = $"Готово: {_newsAll.Count}";
            }
            catch (Exception ex)
            {
                StatusText.Text = "Ошибка";
                MessageBox.Show(ex.Message, "WB Common", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ShowNewsRaw_Click(object sender, RoutedEventArgs e)
        {
            NewsRawExpander.IsExpanded = true;
            NewsRawBox.Text = _newsRaw ?? NewsRawBox.Text;
        }

        private void NewsQueryBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyNewsFilter();

        private void ApplyNewsFilter()
        {
            var q = (NewsQueryBox.Text ?? string.Empty).Trim();

            IEnumerable<NewsRowVm> rows = _newsAll;

            if (!string.IsNullOrWhiteSpace(q))
            {
                rows = rows.Where(x =>
                    (!string.IsNullOrEmpty(x.Title) && x.Title.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(x.Body) && x.Body.Contains(q, StringComparison.OrdinalIgnoreCase)));
            }

            NewsGrid.ItemsSource = rows.ToList();
        }

        private void NewsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (NewsGrid.SelectedItem is NewsRowVm row)
                NewsBodyBox.Text = row.Body ?? "";
            else
                NewsBodyBox.Text = "";
        }

        private static string FormatDt(DateTime? dt)
        {
            if (dt == null) return "";

            var v = dt.Value;

            // WB часто отдаёт без таймзоны. Считаем это UTC.
            if (v.Kind == DateTimeKind.Unspecified)
                v = DateTime.SpecifyKind(v, DateTimeKind.Utc);

            if (v.Kind == DateTimeKind.Utc)
                v = v.ToLocalTime();

            return v.ToString("yyyy-MM-dd HH:mm");
        }

        private sealed class NewsRowVm
        {
            public DateTime? DtUtc { get; set; }
            public string Dt { get; set; } = "";
            public string Title { get; set; } = "";
            public string Body { get; set; } = "";
        }
    }
}

// SellerOps/SellerOps.App/Views/SuppliesPage.xaml.cs
using Microsoft.EntityFrameworkCore;
using SellerOps.App.Data;
using SellerOps.App.Domain;
using SellerOps.App.Services;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace SellerOps.App.Views
{
    public partial class SuppliesPage : Page
    {
        // Сервисы/данные
        private readonly SuppliesService _svc = new();
        private readonly AppDbContext _db = AppDbContext.Instance;

        // Текущие склады (для динамических колонок)
        private List<string> _currentWarehouses = new();

        // Поиск
        private ICollectionView? _view;
        private readonly DispatcherTimer _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        private string _searchQuery = string.Empty;

        public SuppliesPage()
        {
            InitializeComponent();

            // загрузка сохранённых поставок при входе на страницу
            Loaded += async (_, __) =>
            {
                await ReloadSuppliesAsync();
                if (SuppliesList.Items.Count > 0)
                    SuppliesList.SelectedIndex = 0;
            };

            // поиск
            _searchDebounce.Tick += (_, __) => { _searchDebounce.Stop(); ApplySearch(); };
            PreviewKeyDown += SuppliesPage_PreviewKeyDown;

            // кнопка очистки (из XAML)
            if (FindName("ClearSearchBtn") is Button clearBtn)
            {
                clearBtn.Click -= ClearSearchBtn_Click;
                clearBtn.Click += ClearSearchBtn_Click;
            }
        }

        // ===== UI =====

        private async void ImportBtn_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Excel (*.xlsx)|*.xlsx|Все файлы|*.*"
            };
            if (ofd.ShowDialog() != true) return;

            var supply = await _svc.ImportFromExcelAsync(ofd.FileName);
            await ReloadSuppliesAsync();

            // выделим только что импортированную
            var list = SuppliesList.ItemsSource as IEnumerable<Supply>;
            var sel = list?.FirstOrDefault(s => s.Id == supply.Id);
            SuppliesList.SelectedItem = sel;

            await LoadSupplyAsync(supply);
        }

        private async void DeleteBtn_Click(object sender, RoutedEventArgs e)
        {
            if (SuppliesList.SelectedItem is not Supply supply) return;

            if (MessageBox.Show($"Удалить поставку \"{supply.Number}\"?",
                                "Удаление",
                                MessageBoxButton.YesNo,
                                MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            await _svc.DeleteSupplyAsync(supply.Id);
            await ReloadSuppliesAsync();

            ItemsGrid.ItemsSource = null;
            BuildFixedColumnsOnly();
            SetTotals(0, 0, 0);
        }

        private async void SuppliesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SuppliesList.SelectedItem is not Supply supply) return;
            await LoadSupplyAsync(supply);
        }

        private void ScannerBtn_Click(object sender, RoutedEventArgs e)
        {
            // 1) Берём выбранную поставку из списка
            if (SuppliesList.SelectedItem is not SellerOps.App.Domain.Supply supply)
            {
                MessageBox.Show("Сначала выберите поставку слева.", "Сканер", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 2) Создаём страницу сканера с выбранной поставкой
            var page = new ScannerPage(supply);

            // 3) Навигация через NavigationService (если страница во Frame)
            if (this.NavigationService != null)
            {
                this.NavigationService.Navigate(page);
                return;
            }

            // 4) Fallback: пробуем найти Frame в MainWindow (если используете Frame)
            if (Application.Current?.MainWindow is MainWindow mw)
            {
                var frameProp = typeof(MainWindow).GetProperty("MainFrame");
                var frame = frameProp?.GetValue(mw) as System.Windows.Controls.Frame;
                if (frame != null)
                {
                    frame.Navigate(page);
                    return;
                }
            }

            // 5) Последний вариант: открыть как отдельное окно (не ломаем существующий функционал)
            var win = new Window
            {
                Title = $"Сканер — {supply.Number}",
                Content = page,
                Width = 1200,
                Height = 800
            };
            win.Show();
        }


        // ===== DATA =====

        private async Task ReloadSuppliesAsync()
        {
            var supplies = await _db.Supplies
                .OrderByDescending(s => s.CreatedAt)
                .AsNoTracking()
                .ToListAsync();

            SuppliesList.ItemsSource = supplies;
        }

        private async Task LoadSupplyAsync(Supply supply)
        {
            var items = await _db.SupplyItems
                .Where(i => i.SupplyId == supply.Id)
                .AsNoTracking()
                .ToListAsync();

            _currentWarehouses = items
                .Select(i => i.Warehouse)
                .Where(w => !string.IsNullOrWhiteSpace(w))
                .Distinct()
                .OrderBy(w => w)
                .ToList();

            RebuildAllColumns();

            // ==== ДОБАВЛЕНО: расчёт отгруженных количеств по ШК из логов ====
            // Берём все короб-коды, по которым зафиксирован ship
            var shippedBoxes = _db.ScanLogs
                .Where(l => l.SupplyId == supply.Id && l.Kind == "ship" && l.BoxCode != null)
                .Select(l => l.BoxCode!)
                .Distinct()
                .ToList();

            // Сумма Delta по штрихкоду ТОЛЬКО в отгруженных коробах = сколько реально уехало
            var shippedByBarcode = _db.ScanLogs
                .Where(l => l.SupplyId == supply.Id
                            && l.Barcode != null
                            && l.BoxCode != null
                            && shippedBoxes.Contains(l.BoxCode))
                .GroupBy(l => l.Barcode!)
                .Select(g => new { Barcode = g.Key, Qty = g.Sum(x => x.Delta) })
                .ToDictionary(x => x.Barcode, x => x.Qty);

            var rows = BuildRows(items, _currentWarehouses, shippedByBarcode);
            // ================================================================

            var sumTotal = rows.Sum(r => SafeInt(r["Общее"]));
            var sumPicked = rows.Sum(r => SafeInt(r["Собрано"]));
            var sumRemain = rows.Sum(r => SafeInt(r["Остаток"]));

            ItemsGrid.ItemsSource = rows;
            SetTotals(sumTotal, sumPicked, sumRemain);

            // подключаем фильтры/поиск, если нужно (оставляю как было)
            if (ItemsGrid?.ItemsSource is not IEnumerable src) return;
            _view = CollectionViewSource.GetDefaultView(src);
        }

        // ===== COLUMNS =====

        private void BuildFixedColumnsOnly()
        {
            ItemsGrid.Columns.Clear();

            ItemsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "Наименование",
                Binding = new Binding("[Наименование]"),
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                IsReadOnly = true
            });

            ItemsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "Артикул",
                Binding = new Binding("[Артикул]"),
                Width = 130,
                IsReadOnly = true
            });

            ItemsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "ШК",
                Binding = new Binding("[ШК]"),
                Width = 170,
                IsReadOnly = true
            });
        }

        private void RebuildAllColumns()
        {
            BuildFixedColumnsOnly();

            foreach (var wh in _currentWarehouses)
            {
                ItemsGrid.Columns.Add(new DataGridTextColumn
                {
                    Header = wh,
                    Binding = new Binding($"[{wh}]"),
                    Width = 60,
                    IsReadOnly = true
                });
            }

            ItemsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "Общее",
                Binding = new Binding("[Общее]"),
                Width = 70,
                IsReadOnly = true
            });

            ItemsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "Собрано",
                Binding = new Binding("[Собрано]"),
                Width = 80,
                IsReadOnly = true
            });

            ItemsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "Остаток",
                Binding = new Binding("[Остаток]"),
                Width = 70,
                IsReadOnly = true
            });

            ItemsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "Статус",
                Binding = new Binding("[Статус]"),
                Width = 90,
                IsReadOnly = true
            });
        }

        // ===== ROWS =====

        // БЫЛО:
        // private static List<Dictionary<string, object>> BuildRows(List<SupplyItem> items, List<string> warehouses)

        // СТАЛО:
        private static List<Dictionary<string, object>> BuildRows(
            List<SupplyItem> items,
            List<string> warehouses,
            Dictionary<string, int> shippedByBarcode // ← добавили словарь отгрузок
        )
        {
            var skuGroups = items
                .GroupBy(i => new { i.Name, i.Article, i.Barcode })
                .OrderBy(g => g.Key.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var rows = new List<Dictionary<string, object>>();

            foreach (var g in skuGroups)
            {
                var row = new Dictionary<string, object>
                {
                    ["Наименование"] = g.Key.Name ?? "",
                    ["Артикул"] = g.Key.Article ?? "",
                    ["ШК"] = g.Key.Barcode ?? ""
                };

                // по каждому складу — Need
                foreach (var wh in warehouses)
                {
                    var need = g.Where(x => x.Warehouse == wh).Sum(x => x.Need);
                    row[wh] = need;
                }

                var total = g.Sum(x => x.Need);
                var picked = g.Sum(x => x.QtyPicked);
                var remain = Math.Max(0, total - picked);

                // Сколько реально уехало (из ship-коробов)
                int shipped = 0;
                if (!string.IsNullOrEmpty(g.Key.Barcode) && shippedByBarcode.TryGetValue(g.Key.Barcode!, out var s))
                    shipped = Math.Clamp(s, 0, total);

                // ====== КОРРЕКТНОЕ ПРАВИЛО СТАТУСОВ ======
                string status;
                if (shipped >= total)
                {
                    status = "Отгружен";
                }
                else if (shipped > 0)
                {
                    status = "Отгружен частично";
                }
                else if (picked <= 0)
                {
                    status = "Ожидает";
                }
                else if (picked < total)
                {
                    status = "Собран частично";   // ← вот это ключевое исправление
                }
                else // picked == total и ещё ничего не отгружено
                {
                    status = "Собран";
                }
                // =========================================

                row["Общее"] = total;
                row["Собрано"] = picked;
                row["Остаток"] = remain;
                row["Статус"] = status;

                rows.Add(row);
            }

            return rows;
        }


        private static int SafeInt(object? v)
        {
            if (v is int i) return i;
            if (v is long l) return (int)l;
            if (v is double d) return (int)Math.Round(d);
            if (v is float f) return (int)Math.Round(f);
            if (v is decimal m) return (int)Math.Round(m);
            if (v == null) return 0;
            return int.TryParse(v.ToString(), out var n) ? n : 0;
        }

        // ===== ИТОГИ =====
        private void SetTotals(int total, int picked, int remain)
        {
            TxtTotal.Text = total.ToString("N0", CultureInfo.InvariantCulture);
            TxtPicked.Text = picked.ToString("N0", CultureInfo.InvariantCulture);
            TxtRemain.Text = remain.ToString("N0", CultureInfo.InvariantCulture);
        }

        // ===== Поиск =====
        private void TryBindToItemsSource()
        {
            if (ItemsGrid?.ItemsSource is not IEnumerable src) return;
            _view = CollectionViewSource.GetDefaultView(src);
            if (_view != null) _view.Filter = FilterPredicate;
            UpdateSearchInfo();
        }

        private bool FilterPredicate(object obj)
        {
            if (string.IsNullOrWhiteSpace(_searchQuery)) return true;

            var tokens = Regex.Split(_searchQuery.Trim(), @"\s+")
                              .Where(t => t.Length > 0)
                              .ToArray();
            if (tokens.Length == 0) return true;

            // объект строки — Dictionary<string, object>
            if (obj is IDictionary<string, object> dict)
            {
                var flat = string.Join(" ",
                    dict.Values.Select(v =>
                        v is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture)
                                            : v?.ToString() ?? string.Empty));

                foreach (var t in tokens)
                    if (flat.IndexOf(t, StringComparison.OrdinalIgnoreCase) < 0)
                        return false;

                return true;
            }

            // fallback: отражение
            var parts = new List<string>();
            foreach (var p in obj.GetType().GetProperties())
            {
                if (!p.CanRead) continue;
                var val = p.GetValue(obj);
                if (val == null) continue;
                parts.Add(val is IFormattable f
                    ? f.ToString(null, CultureInfo.InvariantCulture)
                    : val.ToString() ?? string.Empty);
            }

            foreach (var t in tokens)
                if (string.Join(" ", parts).IndexOf(t, StringComparison.OrdinalIgnoreCase) < 0)
                    return false;

            return true;
        }

        private void ApplySearch()
        {
            _view?.Refresh();
            UpdateSearchInfo();
        }

        private void UpdateSearchInfo()
        {
            if (SearchInfo == null || _view == null) return;

            int total = 0, visible = 0;

            if (_view.SourceCollection is IEnumerable src)
                foreach (var _ in src) total++;

            foreach (var _ in _view) visible++;

            SearchInfo.Text = string.IsNullOrWhiteSpace(_searchQuery)
                ? $"Всего: {visible}"
                : $"Найдено: {visible} из {total}";
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _searchQuery = SearchBox?.Text ?? string.Empty;
            _searchDebounce.Stop();
            _searchDebounce.Start();
        }

        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                ClearSearchBtn_Click(sender, e);
                e.Handled = true;
            }
        }

        private void ClearSearchBtn_Click(object sender, RoutedEventArgs e)
        {
            if (SearchBox == null) return;
            SearchBox.Text = string.Empty;
            SearchBox.Focus();
        }

        private void SuppliesPage_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                SearchBox?.Focus();
                SearchBox?.SelectAll();
                e.Handled = true;
            }
        }

        // ===== Прочее =====

        private void ItemsGrid_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                SearchBox?.Focus();
                SearchBox?.SelectAll();
                e.Handled = true;
            }
        }

        private void CopyCell_Click(object sender, RoutedEventArgs e)
        {
            var cell = ItemsGrid.CurrentCell;
            if (cell == default || cell.Item is not IDictionary<string, object> dict)
                return;

            var header = cell.Column?.Header?.ToString();
            if (!string.IsNullOrEmpty(header) && dict.TryGetValue(header, out var val))
            {
                Clipboard.SetText(val?.ToString() ?? string.Empty);
            }
        }
    }
}

using Microsoft.EntityFrameworkCore;
using SellerOps.App.Data;
using SellerOps.App.Domain;
using SellerOps.App.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace SellerOps.App.Views
{
    [SupportedOSPlatform("windows")]
    public partial class ScannerPage : Page
    {
        private readonly SuppliesService _svc = new();
        private readonly Supply _supply;

        private string? _activeBoxCode;         // ← ЕДИНСТВЕННОЕ поле с этим именем
        private string _mode = "add";           // add | remove | ship

        private readonly DispatcherTimer _scanTimer;

        private void UpdateActiveBoxLabel()     // ← Оставляем один метод
        {
            // LblBox — TextBlock в верхней панели
            LblBox.Text = _activeBoxCode ?? "—";
        }

        // ===== ВАЛИДАЦИЯ ШК КОРОБА: только латиница/цифры/._- =====
        private static readonly Regex BoxLatinRegex = new(@"^[A-Za-z0-9._-]+$", RegexOptions.Compiled);
        private static bool IsValidBoxCode(string? code) =>
            !string.IsNullOrWhiteSpace(code) && BoxLatinRegex.IsMatch(code!);

        public ScannerPage(Supply supply)
        {
            InitializeComponent();
            _supply = supply;

            _scanTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _scanTimer.Tick += async (_, __) =>
            {
                _scanTimer.Stop();
                var code = TxtBarcode.Text?.Trim();
                if (!string.IsNullOrWhiteSpace(code))
                {
                    await ProcessScanAsync(code);
                    TxtBarcode.Clear();         // ← очищаем поле
                    TxtBarcode.Focus();         // ← возвращаем фокус
                }
            };

            Loaded += async (_, __) =>
            {
                TxtBarcode.Focus();
                LblSupply.Text = _supply.Number;
                await RefreshTotalsGrid();
                await LoadLogs();
                await LoadBoxesList();
                UpdateActiveBoxLabel();
            };
        }

        private void UpdateBoxLabel()
            => LblBox.Text = $"Активный короб: {(_activeBoxCode ?? "—")}";

        // ===== Навигация назад
        private void Back_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (NavigationService != null && NavigationService.CanGoBack)
                {
                    NavigationService.GoBack();
                    return;
                }
            }
            catch { /* ignore */ }

            try
            {
                if (NavigationService != null)
                {
                    NavigationService.Navigate(new SuppliesPage());
                    return;
                }
            }
            catch { /* ignore */ }

            var win = new Window
            {
                Title = "Поставки",
                Content = new SuppliesPage(),
                Width = 1200,
                Height = 800
            };
            win.Show();
            Window.GetWindow(this)?.Close();
        }

        // ===== Вкладки
        private async void BtnLogs_Click(object sender, RoutedEventArgs e)
        {
            LogsArea.Visibility = Visibility.Visible;
            BoxesArea.Visibility = Visibility.Collapsed;
            TotalsArea.Visibility = Visibility.Collapsed;
            await LoadLogs();
        }

        private async void BtnBoxes_Click(object sender, RoutedEventArgs e)
        {
            LogsArea.Visibility = Visibility.Collapsed;
            TotalsArea.Visibility = Visibility.Collapsed;
            BoxesArea.Visibility = Visibility.Visible;

            await LoadBoxesList();
        }


        private void RadioAdd_Checked(object sender, RoutedEventArgs e) => _mode = "add";
        private void RadioRemove_Checked(object sender, RoutedEventArgs e) => _mode = "remove";
        private void RadioShip_Checked(object sender, RoutedEventArgs e) => _mode = "ship";

        // ===== Очистка лога + сброс сборки
        // ===== Очистка лога + сброс сборки
        // Очистка лога + полный сброс сборки по текущей поставке
        private async void ClearLog_Click(object sender, RoutedEventArgs e)
        {
            var db = AppDbContext.Instance;
            var sid = _supply.Id;

            // 1) чистим все записи лога по этой поставке
            db.ScanLogs.RemoveRange(db.ScanLogs.Where(l => l.SupplyId == sid));

            // 2) обнуляем собранное количество по всем позициям поставки
            var toReset = await db.SupplyItems.Where(i => i.SupplyId == sid).ToListAsync();
            foreach (var it in toReset)
                it.QtyPicked = 0;

            await db.SaveChangesAsync();

            // 3) очень важно: сбрасываем "прилипший" активный короб в UI
            _activeBoxCode = null;
            UpdateActiveBoxLabel();   // обновит текст в шапке ("Активный короб: —")

            // 4) перерисовываем всё, что зависит от логов/сборки
            await LoadLogs();         // лог пуст
            await LoadBoxesList();    // список коробов пересоберётся
            await RefreshTotalsGrid();// таблица итогов обновится

            // 5) сообщение пользователю + голос (если используешь)
            LblResult.Text = "Лог очищен. Сборка сброшена.";
            await Speech.SayAsync("Сброс");
        }





        // ===== Логи
        private async Task LoadLogs()
        {
            var db = AppDbContext.Instance;
            var logs = await db.ScanLogs
                               .Where(l => l.SupplyId == _supply.Id)
                               .OrderByDescending(l => l.Ts)
                               .Take(200)
                               .ToListAsync();

            var sb = new StringBuilder();
            foreach (var l in logs.OrderBy(l => l.Ts))
                sb.AppendLine($"{l.Ts:yyyy-MM-dd HH:mm:ss} [{l.Kind}] {l.Message}");

            TxtLog.Text = sb.ToString();
            TxtLog.CaretIndex = TxtLog.Text.Length;
            TxtLog.ScrollToEnd();
        }

        // ===== Короба
        
        // Загрузка списка коробов слева + авто-выбор и показ содержимого справа
        private async Task LoadBoxesList()
        {
            var items = await _svc.GetScannedBoxesWithStatusAsync(_supply);
            BoxesList.ItemsSource = items;                 // элементы { Code, Title } как dynamic
            BoxesList.DisplayMemberPath = "Title";

            // Выделим активный (если знаем), иначе первый
            if (!string.IsNullOrWhiteSpace(_activeBoxCode))
            {
                // ищем элемент с нужным Code — учитываем ExpandoObject
                object? match = null;
                foreach (var it in items)
                {
                    var code = ParseBoxCode(it);
                    if (!string.IsNullOrWhiteSpace(code) &&
                        string.Equals(code, _activeBoxCode, StringComparison.OrdinalIgnoreCase))
                    {
                        match = it;
                        break;
                    }
                }

                if (match != null) BoxesList.SelectedItem = match;
                else if (items.Count > 0) BoxesList.SelectedIndex = 0;
            }
            else if (items.Count > 0)
            {
                BoxesList.SelectedIndex = 0;
            }

            // Явно подгрузим содержимое справа
            await LoadBoxContentsForSelected();
        }


        // Универсальная подгрузка содержимого для текущего выбранного короба
        private async Task LoadBoxContentsForSelected()
        {
            var code = ParseBoxCode(BoxesList.SelectedItem);
            if (string.IsNullOrWhiteSpace(code))
            {
                BoxContentsGrid.ItemsSource = null;
                return;
            }

            // Берём из логов scan всё, где совпадает boxCode
            BoxContentsGrid.ItemsSource = await _svc.GetBoxContentsAsync(_supply, code);
        }
        // Универсальный разбор выбранного элемента ListBox -> boxCode
        private static string? ParseBoxCode(object? item)
        {
            if (item == null) return null;

            // 1) Если это просто строка "WB_XXX — Статус"
            if (item is string s)
            {
                var idx = s.IndexOf('—');           // длинное тире
                var pure = (idx > 0 ? s[..idx] : s).Trim();
                return string.IsNullOrWhiteSpace(pure) ? null : pure;
            }

            // 2) Если это ExpandoObject / dynamic (через IDictionary)
            if (item is System.Collections.Generic.IDictionary<string, object> dict)
            {
                if (dict.TryGetValue("Code", out var v) && v != null)
                    return v.ToString()?.Trim();
            }

            // 3) Любой обычный .NET-объект с публичным свойством Code
            var prop = item.GetType().GetProperty("Code");
            var val = prop?.GetValue(item);
            return val?.ToString()?.Trim();
        }

        // Единственный обработчик SelectionChanged (nullable-параметры)
        private async void BoxesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            await LoadBoxContentsForSelected();
        }




        // ===== Итоги по текущей поставке
        private async Task RefreshTotalsGrid()
        {
            var db = AppDbContext.Instance;

            var items = await db.SupplyItems
                                .Where(i => i.SupplyId == _supply.Id)
                                .AsNoTracking()
                                .ToListAsync();

            var warehouses = items
                .Select(i => i.Warehouse)
                .Where(w => !string.IsNullOrWhiteSpace(w))
                .Distinct()
                .OrderBy(w => w)
                .ToList();

            var groups = items
                .GroupBy(i => new { i.Name, i.Article, i.Barcode })
                .Where(g => g.Sum(x => x.QtyPicked) > 0)
                .ToList();

            var rows = new List<Dictionary<string, object>>();

            foreach (var g in groups)
            {
                var row = new Dictionary<string, object>
                {
                    ["Name"] = g.Key.Name,
                    ["Article"] = g.Key.Article,
                    ["Barcode"] = g.Key.Barcode
                };

                foreach (var wh in warehouses)
                    row[wh] = g.Where(x => x.Warehouse == wh).Sum(x => x.QtyPicked);

                int total = g.Sum(x => x.Need);
                int picked = g.Sum(x => x.QtyPicked);
                int remaining = Math.Max(0, total - picked);

                row["Picked"] = picked;
                row["Need"] = total;
                row["Remaining"] = remaining;

                rows.Add(row);
            }

            TotalsGrid.AutoGenerateColumns = false;
            TotalsGrid.Columns.Clear();

            TotalsGrid.Columns.Add(new DataGridTextColumn { Header = "Наименование", Binding = new Binding("[Name]"), Width = new DataGridLength(1, DataGridLengthUnitType.Star), IsReadOnly = true });
            TotalsGrid.Columns.Add(new DataGridTextColumn { Header = "Артикул", Binding = new Binding("[Article]"), Width = 120, IsReadOnly = true });
            TotalsGrid.Columns.Add(new DataGridTextColumn { Header = "Штрихкод", Binding = new Binding("[Barcode]"), Width = 140, IsReadOnly = true });

            foreach (var wh in warehouses)
                TotalsGrid.Columns.Add(new DataGridTextColumn { Header = wh, Binding = new Binding($"[{wh}]"), Width = 70, IsReadOnly = true });

            TotalsGrid.Columns.Add(new DataGridTextColumn { Header = "Отсканировано", Binding = new Binding("[Picked]"), Width = 90, IsReadOnly = true });
            TotalsGrid.Columns.Add(new DataGridTextColumn { Header = "Нужно", Binding = new Binding("[Need]"), Width = 70, IsReadOnly = true });
            TotalsGrid.Columns.Add(new DataGridTextColumn { Header = "Осталось", Binding = new Binding("[Remaining]"), Width = 90, IsReadOnly = true });
        }

        // ===== Ввод штрихкода
        private void TxtBarcode_TextChanged(object sender, TextChangedEventArgs e)
        {
            _scanTimer.Stop();
            _scanTimer.Start();
        }
        private void BtnTotals_Click(object sender, RoutedEventArgs e)
        {
            // безопасно прекращаем слушать сканер перед навигацией
            _scanTimer.Stop();

            // переход на страницу "Итоги" текущей поставки
            NavigationService?.Navigate(new SuppliesPlanPage(_supply));
        }


        private async void TxtBarcode_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;

            e.Handled = true;
            _scanTimer.Stop();

            var code = TxtBarcode.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(code)) return;

            // Определяем режим из радиокнопок
            string mode =
                (RadioAdd?.IsChecked ?? false) ? "Добавление" :
                (RadioRemove?.IsChecked ?? false) ? "Удаление" :
                (RadioShip?.IsChecked ?? false) ? "Отгрузка" : "Добавление";

            // Сам скан — ВАЖНО: передаём _activeBoxCode
            var (item, result) = await _svc.ApplyScanAdvancedAsync(_supply, code, mode, _activeBoxCode);

            // Показать последний скан
            LblLastScan.Text = code;

            // Разбираем маркер результата и обновляем активный короб
            if (result.StartsWith("Активен короб:", StringComparison.OrdinalIgnoreCase))
            {
                // "Активен короб: WB_XXXX"
                var idx = result.IndexOf(':');
                var bcode = idx >= 0 ? result.Substring(idx + 1).Trim() : null;

                _activeBoxCode = bcode;
                UpdateActiveBoxLabel();

                // По желанию: обновим левый список коробов и данные по выбранному
                await LoadBoxesList();
            }
            else if (result.StartsWith("Короб закрыт", StringComparison.OrdinalIgnoreCase)
                  || result.StartsWith("Короб отгружен", StringComparison.OrdinalIgnoreCase))
            {
                _activeBoxCode = null;
                UpdateActiveBoxLabel();
                await LoadBoxesList();
            }

            // Очистка поля сканера и возврат фокуса
            TxtBarcode.Clear();
            TxtBarcode.Focus();
        }


        // ===== Основная обработка скана
        private async Task ProcessScanAsync(string code)
        {
            code = (code ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(code)) return;

            var db = AppDbContext.Instance;
            LblLastScan.Text = code;

            // Горячие коды режима
            if (string.Equals(code, "ADD", StringComparison.OrdinalIgnoreCase)) { _mode = "add"; LblResult.Text = "Режим: добавление"; await Speech.SayAsync("режим добавление"); return; }
            if (string.Equals(code, "DELETE", StringComparison.OrdinalIgnoreCase)) { _mode = "remove"; LblResult.Text = "Режим: удаление"; await Speech.SayAsync("режим удаление"); return; }
            if (string.Equals(code, "SHIP", StringComparison.OrdinalIgnoreCase)) { _mode = "ship"; LblResult.Text = "Режим: отгрузка"; await Speech.SayAsync("режим отгрузка"); return; }

            // Короб?
            bool looksLikeBox = code.StartsWith("WB_", StringComparison.OrdinalIgnoreCase)
                             || code.StartsWith("OZON_", StringComparison.OrdinalIgnoreCase)
                             || code.Contains("_");

            if (looksLikeBox)
            {
                if (!IsValidBoxCode(code))
                {
                    await Speech.SayAsync("Штрихкод короба должен быть на латинице");
                    return;
                }

                var status = await _svc.GetBoxStatusAsync(_supply, code);
                bool isOpen = string.Equals(status, "Открыт", StringComparison.OrdinalIgnoreCase);

                if (_mode == "ship")
                {
                    if (!string.Equals(status, "Закрыт", StringComparison.OrdinalIgnoreCase))
                    {
                        LblResult.Text = "Сначала закройте короб";
                        await Speech.SayAsync("Сначала закройте короб");
                    }
                    else
                    {
                        await _svc.MarkBoxShippedAsync(_supply, code);
                        LblResult.Text = "Короб отгружен";
                        await Speech.SayAsync($"Короб {BoxWarehouse(code)} отгружен");
                    }
                }
                else
                {
                    if (!isOpen)
                    {
                        await _svc.MarkBoxOpenedAsync(_supply, code);
                        LblResult.Text = "Короб открыт";
                        await Speech.SayAsync($"Короб {BoxWarehouse(code)} открыт");
                    }
                    else
                    {
                        await _svc.MarkBoxClosedAsync(_supply, code);
                        LblResult.Text = "Короб закрыт";
                        await Speech.SayAsync($"Короб {BoxWarehouse(code)} закрыт");
                    }
                }

                _activeBoxCode = code;
                UpdateBoxLabel();

                await LoadLogs();
                await LoadBoxesList();
                await RefreshTotalsGrid();
                return;
            }

            // Товар — нужен выбранный короб
            if (string.IsNullOrWhiteSpace(_activeBoxCode))
            {
                await _svc.AddLogAsync(_supply, "warn", $"ШК товара без выбранного короба: {code}");
                await ShowInfoForBarcodeAsync(code);
                LblResult.Text = "Сначала отсканируйте короб, чтобы выбрать склад.";
                await Speech.SayAsync("Сначала отсканируйте короб, чтобы выбрать склад");
                return;
            }

            // Сначала очищаем строку складов (не «залипала» от предыдущего)
            LblWarehouses.Text = string.Empty;

            // Для голосового уведомления — проверим, есть ли такой ШК в текущей поставке
            bool inCurrentSupply = await db.SupplyItems.AnyAsync(i => i.SupplyId == _supply.Id && i.Barcode == code);
            if (!inCurrentSupply)
                await Speech.SayAsync("Товар не из текущей поставки");

            // >>> REMOVE: предварительная проверка для режима удаления (товарный скан)
            if (_mode == "remove")
            {
                // Сколько ЭТОГО ШК реально лежит в ЭТОМ активном коробе? (+1/-1 по scan-логам)
                var qtyInThisBox = await db.ScanLogs
                    .Where(l => l.SupplyId == _supply.Id
                                && l.Kind == "scan"
                                && l.BoxCode == _activeBoxCode
                                && l.Barcode == code)
                    .SumAsync(l => l.Delta);

                if (qtyInThisBox <= 0)
                {
                    var wh = BoxWarehouse(_activeBoxCode!);
                    await _svc.AddLogAsync(_supply, "info", "В этом коробе такого товара нет", code, wh);
                    LblResult.Text = "В этом коробе такого товара нет";
                    await LoadLogs(); // покажем пользователю запись сразу
                                      // Возврат фокуса, чтобы можно было сразу сканировать дальше
                    TxtBarcode.Clear();
                    TxtBarcode.Focus();
                    return;
                }
            }
            // <<< REMOVE: конец предварительной проверки

            // Применяем скан (добавление/удаление/отгрузка)
            // Преобразуем внутренний ui-режим в «человеческий» режим, который ждёт сервис
            string serviceMode = _mode?.ToLowerInvariant() switch
            {
                "remove" => "Удаление",
                "ship" => "Отгрузка",
                _ => "Добавление"
            };

            var (item, phrase) = await _svc.ApplyScanAdvancedAsync(_supply, code, serviceMode, _activeBoxCode);


            // Обновление карточки + дополнение логов
            if (item != null)
            {
                LblSupply.Text = _supply.Number;
                LblArticle.Text = item.Article;
                LblItem.Text = item.Name;
                LblResult.Text = phrase;

                var lastLog = await db.ScanLogs
                                      .Where(l => l.SupplyId == _supply.Id)
                                      .OrderByDescending(l => l.Ts)
                                      .FirstOrDefaultAsync();
                if (lastLog != null && string.Equals(lastLog.Kind, "scan", StringComparison.OrdinalIgnoreCase))
                {
                    if (!lastLog.Message.Contains("Арт:", StringComparison.OrdinalIgnoreCase))
                    {
                        lastLog.Message = $"{lastLog.Message} | Арт: {item.Article}; ШК: {item.Barcode}";
                        await db.SaveChangesAsync();
                    }
                }

                // >>> REMOVE: голос после успешного удаления
                if (_mode == "remove")
                    await Speech.SayAsync("Удалён");
                // <<< REMOVE
            }
            else
            {
                LblResult.Text = phrase;
            }

            // Инфо-блоки
            var effectiveBarcode = item?.Barcode ?? code;

            if (item != null && item.SupplyId == _supply.Id)
                await FillCurrentSupplyWarehousesAsync(effectiveBarcode);
            else
                LblWarehouses.Text = string.Empty;

            await FillOtherSuppliesAsync(effectiveBarcode);

            // Озвучка обратного отсчёта + «ноль»
            var digitsOnly = Regex.Match(LblResult.Text ?? "", @"(\d+)(?!.*\d)").Value;
            if (!string.IsNullOrEmpty(digitsOnly))
            {
                await Speech.SayAsync(digitsOnly);
                if (digitsOnly == "0")
                    await Speech.SayAsync("Товар для этого склада собран");
            }

            // Озвучка «склад собран» / «вся поставка собрана» по текущему ШК
            if (item != null && item.SupplyId == _supply.Id)
            {
                var same = await db.SupplyItems
                                   .Where(i => i.SupplyId == _supply.Id && i.Barcode == item.Barcode)
                                   .ToListAsync();

                var wh = BoxWarehouse(_activeBoxCode!);

                int needWh = same.Where(x => string.Equals(x.Warehouse, wh, StringComparison.OrdinalIgnoreCase)).Sum(x => x.Need);
                int pickedWh = same.Where(x => string.Equals(x.Warehouse, wh, StringComparison.OrdinalIgnoreCase)).Sum(x => x.QtyPicked);
                if (needWh > 0 && pickedWh >= needWh)
                    await Speech.SayAsync("Товар для склада собран");

                int needTotal = same.Sum(x => x.Need);
                int pickedTotal = same.Sum(x => x.QtyPicked);
                if (needTotal > 0 && pickedTotal >= needTotal)
                    await Speech.SayAsync("Товар собран на всю поставку");
            }

            // Обновление UI-частей списков
            await LoadLogs();
            await LoadBoxesList();
            await RefreshTotalsGrid();

            // Возврат фокуса
            TxtBarcode.Clear();
            TxtBarcode.Focus();
        }


        // Вспомогательное извлечение «краткого» склада из кода короба (WB_CFO1 -> CFO1)
        private static string BoxWarehouse(string boxCode)
        {
            int i = boxCode.IndexOf('_');
            if (i < 0 || i + 1 >= boxCode.Length) return boxCode;
            return boxCode.Substring(i + 1);
        }

        // Подсказки по товару (в этой и других поставках)
        private async Task ShowInfoForBarcodeAsync(string barcode)
        {
            var db = AppDbContext.Instance;

            var inCurrent = await db.SupplyItems
                .Where(i => i.SupplyId == _supply.Id && i.Barcode == barcode)
                .ToListAsync();

            if (inCurrent.Count > 0)
            {
                LblSupply.Text = _supply.Number;
                LblArticle.Text = inCurrent.First().Article;
                LblItem.Text = inCurrent.First().Name;

                await FillCurrentSupplyWarehousesAsync(barcode);
                await FillOtherSuppliesAsync(barcode);
                return;
            }

            var other = await db.SupplyItems.Include(i => i.Supply)
                .Where(i => i.Barcode == barcode && i.SupplyId != _supply.Id)
                .OrderByDescending(i => i.Supply.CreatedAt)
                .ToListAsync();

            if (other.Count > 0)
            {
                var latest = other.GroupBy(i => i.Supply)
                                  .OrderByDescending(g => g.Key.CreatedAt)
                                  .First();

                LblSupply.Text = latest.Key.Number;
                LblArticle.Text = latest.First().Article;
                LblItem.Text = latest.First().Name;

                // Для чужой поставки строка «Склады (эта поставка)» не показывается
                LblWarehouses.Text = string.Empty;
                await FillOtherSuppliesAsync(barcode);
            }
            else
            {
                LblSupply.Text = _supply.Number;
                LblArticle.Text = "";
                LblItem.Text = "Товар не найден в поставках";
                LblWarehouses.Text = "";
                LblOtherSupplies.Text = "—";
            }
        }

        // Текущая поставка — разложение по складам (Need)
        private async Task FillCurrentSupplyWarehousesAsync(string barcode)
        {
            var db = AppDbContext.Instance;

            var same = await db.SupplyItems
                               .Where(i => i.SupplyId == _supply.Id && i.Barcode == barcode)
                               .ToListAsync();

            if (same.Count == 0)
            {
                LblWarehouses.Text = "";
                return;
            }

            var byWh = same
                .GroupBy(x => x.Warehouse)
                .Select(g => new
                {
                    Wh = g.Key,
                    Remaining = g.Sum(x => Math.Max(0, x.Need - x.QtyPicked))
                })
                .Where(x => x.Remaining > 0)
                .OrderBy(x => x.Wh)
                .ToList();

            LblWarehouses.Text = byWh.Count == 0
                ? "Для всех складов собрано"
                : string.Join(", ", byWh.Select(x => $"{x.Wh}:{x.Remaining}"));
        }

        // Другие поставки с остатком — по складам
        private async Task FillOtherSuppliesAsync(string barcode)
        {
            var db = AppDbContext.Instance;

            var items = await db.SupplyItems
                .Include(i => i.Supply)
                .Where(i => i.Barcode == barcode && i.SupplyId != _supply.Id)
                .AsNoTracking()
                .ToListAsync();

            var bySupply = items
                .GroupBy(i => i.Supply)
                .Select(g => new
                {
                    Supply = g.Key,
                    PerWh = g.GroupBy(x => x.Warehouse)
                             .Select(wg => new
                             {
                                 Wh = wg.Key,
                                 Rem = wg.Sum(x => Math.Max(0, x.Need - x.QtyPicked))
                             })
                             .Where(x => x.Rem > 0)
                             .OrderBy(x => x.Wh)
                             .ToList()
                })
                .Where(x => x.PerWh.Any())
                .OrderByDescending(x => x.Supply.CreatedAt)
                .ToList();

            if (!bySupply.Any())
            {
                LblOtherSupplies.Text = "—";
                return;
            }

            LblOtherSupplies.Text = string.Join(", ",
                bySupply.Select(s => $"{s.Supply.Number} ({string.Join(", ", s.PerWh.Select(p => $"{p.Wh}:{p.Rem}"))})"));
        }

        // (оставлен на случай использования где-то ещё)
        private async Task<List<(string SupplyNumber, int Remaining)>> GetOtherSuppliesWithRemainingAsync(string barcode)
        {
            var db = AppDbContext.Instance;

            var items = await db.SupplyItems
                .Include(i => i.Supply)
                .Where(i => i.Barcode == barcode && i.SupplyId != _supply.Id)
                .AsNoTracking()
                .ToListAsync();

            var bySupply = items
                .GroupBy(i => i.Supply)
                .Select(g => new
                {
                    Supply = g.Key,
                    Remaining = g.Sum(x => Math.Max(0, x.Need - x.QtyPicked))
                })
                .Where(x => x.Remaining > 0)
                .OrderByDescending(x => x.Supply.CreatedAt)
                .ToList();

            return bySupply
                .Select(x => (x.Supply.Number, x.Remaining))
                .ToList();
        }
    }
}

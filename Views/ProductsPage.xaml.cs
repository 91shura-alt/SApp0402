using ExcelDataReader;
using Microsoft.EntityFrameworkCore;
using Microsoft.Win32;
using SellerOps.App.Data;
using SellerOps.App.Domain;
using SellerOps.App.Services;
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;

namespace SellerOps.App.Views
{
    public partial class ProductsPage : Page
    {
        public ProductsPage()
        {
            InitializeComponent();

            // Горячая клавиша/контекстное меню "Копировать"
            CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, CopyCells));

            try
            {
                LoadAll();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Ошибка загрузки товаров", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Безопасная установка ItemsSource: очищаем Items перед привязкой
        private void SetGridItemsSource(IEnumerable<WbProduct> items)
        {
            try
            {
                if (ProductsGrid.Items.Count > 0)
                    ProductsGrid.Items.Clear();

                ProductsGrid.ItemsSource = null; // сброс старой привязки
                ProductsGrid.ItemsSource = items;
            }
            catch
            {
                ProductsGrid.ItemsSource = null;
                ProductsGrid.Items.Clear();
                ProductsGrid.ItemsSource = items;
            }
        }

        private void LoadAll()
        {
            var db = AppDbContext.Instance;

            var data = db.WbProducts
                .OrderBy(x => x.Title)
                .Take(1000)
                .ToList();

            SetGridItemsSource(data);
        }

        private void Search_Click(object sender, RoutedEventArgs e)
        {
            var q = (SearchBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(q))
            {
                LoadAll(); // твой метод, который грузит всё
                return;
            }

            var db = AppDbContext.Instance;

            // тянем данные в память и фильтруем без учёта регистра
            var data = db.WbProducts.AsNoTracking().ToList();

            if (long.TryParse(q, out var nm))
            {
                data = data.Where(x =>
                       x.NmId == nm
                    || ContainsCI(x.Barcode, q)
                    || ContainsCI(x.VendorCode, q)
                    || ContainsCI(x.Title, q)).ToList();
            }
            else
            {
                data = data.Where(x =>
                       ContainsCI(x.Title, q)
                    || ContainsCI(x.Brand, q)
                    || ContainsCI(x.Subject, q)
                    || ContainsCI(x.VendorCode, q)
                    || ContainsCI(x.Barcode, q))
                    .ToList();
            }

            ProductsGrid.ItemsSource = data
                .OrderBy(x => x.Title)
                .Take(1000)
                .ToList();
        }

        private async void Sync_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var svc = new WbCatalogService();
                var updated = await svc.SyncProductsAsync();

                LoadAll();

                MessageBox.Show(
                    $"Синхронизация завершена.\nОбновлено/добавлено: {updated}.",
                    "Диагностика",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Ошибка синхронизации", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenLogs_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Ошибка открытия логов", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Клик по названию — открыть карточку товара
        private void TitleCell_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is WbProduct p)
            {
                NavigationService?.Navigate(new ProductDetailsPage(p.NmId));
                e.Handled = true; // чтобы не мешало выделению/копированию
            }
        }

        // ====== Копирование ячеек ======
        private void CopyCells(object sender, ExecutedRoutedEventArgs e)
        {
            if (ProductsGrid.SelectedCells == null || ProductsGrid.SelectedCells.Count == 0)
                return;

            var rows = ProductsGrid.SelectedCells
                .GroupBy(c => c.Item)
                .Select(g => g.OrderBy(c => c.Column.DisplayIndex).ToList())
                .ToList();

            if (rows.Count == 1 && rows[0].Count == 1)
            {
                var cell = rows[0][0];
                Clipboard.SetText(GetCellText(cell.Column, cell.Item));
                return;
            }

            var lines = rows.Select(row =>
                string.Join("\t", row.Select(c => GetCellText(c.Column, c.Item)))
            );

            Clipboard.SetText(string.Join(Environment.NewLine, lines));
        }

        private static string GetCellText(DataGridColumn col, object item)
        {
            if (col is DataGridBoundColumn bc && bc.Binding is System.Windows.Data.Binding b && b.Path != null)
            {
                var path = b.Path.Path;
                if (!string.IsNullOrEmpty(path))
                {
                    var prop = item.GetType().GetProperty(path,
                                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                    var val = prop?.GetValue(item);
                    return val?.ToString() ?? string.Empty;
                }
            }

            var fe = col.GetCellContent(item);
            if (fe is TextBlock tb) return tb.Text ?? string.Empty;
            if (fe is TextBox tbx) return tbx.Text ?? string.Empty;

            return string.Empty;
        }
        private async void ImportCost_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Filter = "Файлы Excel/CSV|*.xlsx;*.xls;*.csv|Все файлы|*.*"
                };
                if (dlg.ShowDialog() != true) return;

                var rows = await CostFileParser.ReadAsync(dlg.FileName); // твой парсер

                int updated = 0;
                using (var db = new AppDbContext())
                {
                    foreach (var r in rows)
                    {
                        // сопоставление: по nmId / штрихкоду / артикулу поставщика
                        var p = db.WbProducts.FirstOrDefault(x =>
                                   (r.NmId.HasValue && x.NmId == r.NmId) ||
                                   (!string.IsNullOrEmpty(r.Barcode) && x.Barcode == r.Barcode) ||
                                   (!string.IsNullOrEmpty(r.VendorCode) && x.VendorCode == r.VendorCode));

                        if (p == null) continue;

                        p.Cost = r.Cost;
                        // если добавил поле:
                        // p.CostUpdatedAt = DateTime.UtcNow;

                        updated++;
                    }

                    db.SaveChanges();
                }

                MessageBox.Show($"Импорт завершён. Обновлено: {updated}", "Импорт", MessageBoxButton.OK,
                    MessageBoxImage.Information);

                LoadAll(); // чтобы перечитать список в UI
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Ошибка импорта", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }


        private sealed class CostRecord
        {
            public string? NmId { get; set; }       // Артикул WB
            public string? VendorCode { get; set; }  // Артикул поставщика
            public string? Barcode { get; set; }     // Штрихкод
            public decimal? Cost { get; set; }
        }

        private IEnumerable<CostRecord> ReadCsvCosts(string path)
        {
            using var sr = new StreamReader(path);
            string? header = sr.ReadLine();
            if (header == null) yield break;
            var cols = header.Split(';', '\t', ',');
            int idxNm = IndexOf(cols, "nm", "артикул wb", "артикул_wb", "nmId", "nmID");
            int idxVend = IndexOf(cols, "vendor", "артикул поставщика", "артикул", "vendorcode");
            int idxBar = IndexOf(cols, "barcode", "штрихкод", "sku");
            int idxCost = IndexOf(cols, "cost", "себестоимость", "цена");

            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                var p = line.Split(';', '\t', ',');
                var rec = new CostRecord
                {
                    NmId = Grab(p, idxNm),
                    VendorCode = Grab(p, idxVend),
                    Barcode = Grab(p, idxBar),
                    Cost = TryParseDec(Grab(p, idxCost))
                };
                yield return rec;
            }

            static int IndexOf(string[] arr, params string[] keys)
            {
                for (int i = 0; i < arr.Length; i++)
                {
                    var a = arr[i].Trim().ToLowerInvariant();
                    if (keys.Any(k => a.Contains(k))) return i;
                }
                return -1;
            }
            static string? Grab(string[] a, int i) => i >= 0 && i < a.Length ? a[i]?.Trim() : null;
            static decimal? TryParseDec(string? s)
            {
                if (string.IsNullOrWhiteSpace(s)) return null;
                s = s.Replace(" ", "").Replace(",", "."); // 1 234,56 -> 1234.56
                return decimal.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : (decimal?)null;
            }
        }

        private CostRecord ParseCostRow(DataRow row, DataColumnCollection cols)
        {
            string Get(params string[] keys)
            {
                foreach (DataColumn c in cols)
                {
                    var name = (c.ColumnName ?? "").Trim().ToLowerInvariant();
                    if (keys.Any(k => name.Contains(k)))
                        return (row[c]?.ToString() ?? "").Trim();
                }
                return "";
            }

            decimal? ParseDec(string s)
            {
                if (string.IsNullOrWhiteSpace(s)) return null;
                s = s.Replace(" ", "").Replace(",", ".");
                return decimal.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : (decimal?)null;
            }

            return new CostRecord
            {
                NmId = Get("nm", "артикул wb", "артикул_wb", "nmid", "nm id"),
                VendorCode = Get("vendor", "артикул поставщика", "артикул", "vendorcode"),
                Barcode = Get("barcode", "штрихкод", "sku"),
                Cost = ParseDec(Get("cost", "себестоимость", "цена"))
            };
        }
        private static bool ContainsCI(string? source, string term)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(term)) return false;
            return source.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private bool ApplyCostRecord(AppDbContext db, CostRecord rec)
        {
            if (rec == null || rec.Cost == null) return false;

            WbProduct? p = null;

            // 1) по штрихкоду
            if (!string.IsNullOrWhiteSpace(rec.Barcode))
                p = db.WbProducts.FirstOrDefault(x => x.Barcode == rec.Barcode);

            // 2) по артикулу WB
            if (p == null && long.TryParse(rec.NmId, out var nm))
                p = db.WbProducts.FirstOrDefault(x => x.NmId == nm);

            // 3) по артикулу поставщика
            if (p == null && !string.IsNullOrWhiteSpace(rec.VendorCode))
                p = db.WbProducts.FirstOrDefault(x => x.VendorCode == rec.VendorCode);

            if (p == null) return false;

            p.Cost = rec.Cost;
            return true;
        }

    }
}

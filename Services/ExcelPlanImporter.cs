using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClosedXML.Excel;
using SellerOps.App.Data;
using SellerOps.App.Domain;

namespace SellerOps.App.Services
{
    public class ExcelPlanImporter
    {
        private readonly AppDbContext _db = AppDbContext.Instance;

        /// <summary>
        /// Импортирует поставку из Excel (ClosedXML).
        /// Формат ожидается такой:
        /// A: Наименование
        /// B: Артикул
        /// C: Штрихкод
        /// D: Количество (общее)
        /// E..: любые склады (MSK, CFO, YUG, Самара и т.п.)
        /// </summary>
        public async Task<Supply> ImportFromExcelAsync(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException("Файл Excel не найден.", path);

            using var wb = new XLWorkbook(path);
            var ws = wb.Worksheets.First();

            // Номер поставки — из имени файла (без расширения)
            var number = Path.GetFileNameWithoutExtension(path)?.Trim();
            if (string.IsNullOrWhiteSpace(number))
                number = $"SUP-{DateTime.Now:yyyyMMdd-HHmmss}";

            var supply = new Supply
            {
                Number = number,
                CreatedAt = DateTime.Now,
                Items = new List<SupplyItem>()
            };

            _db.Supplies.Add(supply);
            await _db.SaveChangesAsync(); // чтобы получить Id у supply

            // Заголовки в первой строке
            // Ищем динамические склады начиная с 5-й колонки (E)
            var headers = new List<string>();
            int col = 1;
            while (true)
            {
                var h = ws.Cell(1, col).GetValue<string>()?.Trim();
                if (string.IsNullOrWhiteSpace(h)) break;
                headers.Add(h);
                col++;
                if (col > 200) break;
            }

            int COL_NAME = 1;     // A
            int COL_ART = 2;     // B
            int COL_BC = 3;     // C
            int COL_TOT = 4;     // D
            int COL_WH0 = 5;     // E.. динамические склады

            // Список фактических складов из шапки
            var whHeaders = new List<string>();
            for (int c = COL_WH0; c <= headers.Count; c++)
            {
                var h = headers[c - 1]; // headers 0-based
                if (string.IsNullOrWhiteSpace(h)) break;

                // пропускаем возможные "служебные" заголовки
                if (EqualsIgnore(h, "Количество") ||
                    EqualsIgnore(h, "Общее") ||
                    EqualsIgnore(h, "Итого"))
                    continue;

                whHeaders.Add(h);
            }

            // Чтение строк
            int row = 2;
            while (true)
            {
                var name = GetString(ws.Cell(row, COL_NAME));
                var article = GetString(ws.Cell(row, COL_ART));
                var barcode = GetString(ws.Cell(row, COL_BC));

                // Пустая строка — конец данных
                if (string.IsNullOrWhiteSpace(name) &&
                    string.IsNullOrWhiteSpace(article) &&
                    string.IsNullOrWhiteSpace(barcode))
                    break;

                int total = GetInt(ws.Cell(row, COL_TOT));

                // Значения по каждому складу
                var perWh = new List<(string wh, int need)>();
                for (int c = 0; c < whHeaders.Count; c++)
                {
                    string whName = whHeaders[c];
                    int val = GetInt(ws.Cell(row, COL_WH0 + c));
                    if (val > 0)
                        perWh.Add((whName, val));
                }

                if (perWh.Count == 0)
                {
                    // В таблице нет разбиения по складам — используем "Общее"
                    if (total > 0)
                    {
                        supply.Items.Add(new SupplyItem
                        {
                            SupplyId = supply.Id,
                            Name = name,
                            Article = article,
                            Barcode = barcode,
                            Warehouse = "Общее",
                            Need = total,
                            QtyPicked = 0,
                            Status = "Ожидает"
                        });
                    }
                }
                else
                {
                    foreach (var (wh, need) in perWh)
                    {
                        supply.Items.Add(new SupplyItem
                        {
                            SupplyId = supply.Id,
                            Name = name,
                            Article = article,
                            Barcode = barcode,
                            Warehouse = wh, // фактическое имя склада из файла
                            Need = need,
                            QtyPicked = 0,
                            Status = "Ожидает"
                        });
                    }
                }

                row++;
                if (row > 200000) break; // защита от зацикливания
            }

            await _db.SaveChangesAsync();
            return supply;

            // ==== локальные помощники ====

            static bool EqualsIgnore(string a, string b)
                => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

            static string GetString(IXLCell cell)
            {
                var s = cell.GetString();
                return string.IsNullOrWhiteSpace(s) ? string.Empty : s.Trim();
            }

            static int GetInt(IXLCell cell)
            {
                // ClosedXML может вернуть число как string, decimal и т.п.
                if (cell.TryGetValue<int>(out var i)) return i;
                var s = cell.GetValue<string>();
                if (int.TryParse(s?.Trim(), out var j)) return j;
                // иногда числа хранятся как double (например "15.0")
                if (double.TryParse(s?.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Any,
                                    System.Globalization.CultureInfo.InvariantCulture, out var d))
                    return (int)Math.Round(d);
                return 0;
            }
        }
    }
}

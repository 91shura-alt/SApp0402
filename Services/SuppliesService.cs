using System;
using System.Collections.Generic;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SellerOps.App.Data;
using SellerOps.App.Domain;

namespace SellerOps.App.Services
{
    /// <summary>
    /// Фасад работы с БД, импортом, сканером и логами.
    /// Содержит ВСЕ методы, которые уже вызываются из страниц.
    /// </summary>
    public class SuppliesService
    {
        private readonly AppDbContext _db;
        public SuppliesService(AppDbContext? db = null) { _db = db ?? AppDbContext.Instance; }

        // ---------------- Поставки ----------------

        public Task<List<Supply>> GetSuppliesAsync()
            => Task.FromResult(_db.Supplies.OrderByDescending(s => s.CreatedAt).ToList());

        public Task<Supply?> GetSupplyAsync(Guid id)
            => Task.FromResult(_db.Supplies.FirstOrDefault(s => s.Id == id));

        public async Task<Supply> EnsureSupplyAsync(string number)
        {
            var s = _db.Supplies.FirstOrDefault(x => x.Number == number);
            if (s != null) return s;
            s = new Supply { Number = number, CreatedAt = DateTime.UtcNow };
            _db.Supplies.Add(s);
            await _db.SaveChangesAsync().ConfigureAwait(false);
            return s;
        }

        public async Task DeleteSupplyAsync(Guid id)
        {
            var s = _db.Supplies.FirstOrDefault(x => x.Id == id);
            if (s == null) return;
            var items = _db.SupplyItems.Where(i => i.SupplyId == id).ToList();
            var logs = _db.ScanLogs.Where(l => l.SupplyId == id).ToList();
            _db.SupplyItems.RemoveRange(items);
            _db.ScanLogs.RemoveRange(logs);
            _db.Supplies.Remove(s);
            await _db.SaveChangesAsync().ConfigureAwait(false);
        }

        // ---------------- Импорт ----------------

        public async Task<Supply> ImportFromExcelAsync(string filePath)
        {
            // Импорт непосредственно из Excel через ClosedXML
            var importer = new ExcelPlanImporter();
            var supply = await importer.ImportFromExcelAsync(filePath).ConfigureAwait(false);

            // Ничего больше не делаем: импортер уже создал Supply и его Items в БД.
            // Возвращаем свежесозданную поставку (со всеми позициями).
            return supply;
        }

        // ---------------- Короба / статусы ----------------

        public string? ResolveWarehouseByBox(string? boxCode)
        {
            if (string.IsNullOrWhiteSpace(boxCode)) return null;
            var s = boxCode.ToUpperInvariant();
            if (s.Contains("CFO") || s.Contains("MSK") || s.Contains("МСК")) return "CFO";
            if (s.Contains("SAM") || s.Contains("SAMARA") || s.Contains("САМ")) return "Samara";
            if (s.Contains("YUG") || s.Contains("ЮГ") || s.Contains("_UG")) return "YUG";
            return null;
        }

        public Task<string> GetBoxStatusAsync(Supply supply, string? boxCode)
        {
            if (string.IsNullOrWhiteSpace(boxCode)) return Task.FromResult("—");

            var last = _db.ScanLogs
                .Where(l => l.SupplyId == supply.Id
                         && l.BoxCode == boxCode
                         && (l.Kind == "box" || l.Kind == "ship"))
                .OrderByDescending(l => l.Ts)
                .FirstOrDefault();

            if (last == null) return Task.FromResult("—");
            if (last.Kind == "ship") return Task.FromResult("Отгружен");
            return Task.FromResult(last.Message.Contains("Закрыт", StringComparison.OrdinalIgnoreCase) ? "Закрыт" : "Открыт");
        }



        public Task MarkBoxOpenedAsync(Supply supply, string boxCode)
            => AddLogAsync(supply, "box", $"Открыт короб {boxCode}", boxCode: boxCode);

        public Task MarkBoxClosedAsync(Supply supply, string boxCode)
            => AddLogAsync(supply, "box", $"Закрыт короб {boxCode}", boxCode: boxCode);

        public Task MarkBoxShippedAsync(Supply supply, string boxCode)
            => AddLogAsync(supply, "ship", $"Отгружен короб {boxCode}", boxCode: boxCode);

        public Task<List<string>> GetScannedBoxesAsync(Supply supply)
        {
            var list = _db.ScanLogs
                .Where(l => l.SupplyId == supply.Id && l.BoxCode != null)
                .OrderBy(l => l.BoxCode)
                .Select(l => l.BoxCode!)
                .Distinct()
                .ToList();
            return Task.FromResult(list);
        }

        /// <summary>
        /// Содержимое короба по логам (сумма дельт), агрегировано по ШК.
        /// Возвращает список анонимных объектов: Name, Article, Barcode, Qty.
        /// </summary>
        public Task<List<dynamic>> GetBoxContentsAsync(Supply supply, string? boxCode)
        {
            if (string.IsNullOrWhiteSpace(boxCode))
                return Task.FromResult(new List<dynamic>());

            var content = _db.ScanLogs
                .Where(l => l.SupplyId == supply.Id
                         && l.BoxCode == boxCode
                         && l.Barcode != null
                         && l.Kind == "scan")
                .GroupBy(l => l.Barcode!)
                .Select(g => new { Barcode = g.Key, Qty = g.Sum(x => x.Delta) })
                .Where(x => x.Qty != 0)
                .ToList();

            var barcodes = content.Select(c => c.Barcode).ToHashSet();
            var infos = _db.SupplyItems
                .Where(i => i.SupplyId == supply.Id && barcodes.Contains(i.Barcode))
                .GroupBy(i => i.Barcode)
                .Select(g => g.First())
                .ToDictionary(x => x.Barcode, x => x);

            var result = new List<dynamic>();
            foreach (var c in content)
            {
                infos.TryGetValue(c.Barcode, out var info);
                dynamic d = new ExpandoObject();
                d.Name = info?.Name ?? "";
                d.Article = info?.Article ?? "";
                d.Barcode = c.Barcode;
                d.Qty = c.Qty;
                result.Add(d);
            }
            return Task.FromResult(result);
        }






        // Возвращает список коробов со статусом для левого списка:
        // Title = "WB_CFO1 — Открыт/Закрыт/Отгружен", Code = "WB_CFO1"
        public Task<List<dynamic>> GetScannedBoxesWithStatusAsync(Supply supply)
        {
            // Валидируем код короба (WB_... или OZON_..., без «склеек» вида ...WB_CFO1)
            bool IsValidBox(string? s) =>
                !string.IsNullOrWhiteSpace(s)
                && (s!.StartsWith("WB_", StringComparison.OrdinalIgnoreCase)
                    || s.StartsWith("OZON_", StringComparison.OrdinalIgnoreCase))
                && s.Count(c => c == '_') == 1
                && s.Length <= 32;

            // 1) Берём кандидатов из БД (только события box/ship по поставке)
            var raw = _db.ScanLogs
                .Where(l => l.SupplyId == supply.Id
                            && l.BoxCode != null
                            && (l.Kind == "box" || l.Kind == "ship"))
                .ToList(); // ← материализуем, дальше можно вызывать любые .NET методы

            // 2) Оставляем только валидные коды, берём последнее событие по каждому коробу
            var lastByBox = raw
                .Where(l => IsValidBox(l.BoxCode))
                .GroupBy(l => l.BoxCode!)
                .Select(g => g.OrderByDescending(x => x.Ts).First())
                .ToList();

            // 3) Определяем статус по последнему событию
            string StatusOf(ScanLog last)
            {
                if (last.Kind == "ship") return "Отгружен";
                return last.Message?.IndexOf("Закрыт", StringComparison.OrdinalIgnoreCase) >= 0
                    ? "Закрыт"
                    : "Открыт";
            }

            // 4) Готовим элементы для ListBox слева: Title + Code
            var result = lastByBox
                .Select(l =>
                {
                    dynamic d = new System.Dynamic.ExpandoObject();
                    d.Code = l.BoxCode!;
                    d.Title = $"{l.BoxCode} — {StatusOf(l)}";
                    return d;
                })
                .OrderBy(d => (string)d.Code)
                .Cast<dynamic>()
                .ToList();

            return Task.FromResult(result);
        }



        // ---------------- Сканирование ----------------

        public async Task<(SupplyItem? item, string result)> ApplyScanAsync(
    Supply supply, string code, string mode, string? activeBoxCode)
        {
            code = (code ?? string.Empty).Trim();
            mode = (mode ?? "Добавление").Trim();

            // Режимы
            bool isRemove = string.Equals(mode, "Удаление", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(mode, "remove", StringComparison.OrdinalIgnoreCase);

            bool isShip = string.Equals(mode, "Отгрузка", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(mode, "ship", StringComparison.OrdinalIgnoreCase);

            bool isAdd = !isRemove && !isShip;

            // Короб или товар?
            bool isBox = code.StartsWith("WB_", StringComparison.OrdinalIgnoreCase)
                      || code.StartsWith("OZON_", StringComparison.OrdinalIgnoreCase)
                      || Regex.IsMatch(code, @"^[A-Za-z]+_[A-Za-z]+[0-9]+$");

            // --------------------- Скан КОРОБА ---------------------
            if (isBox)
            {
                var status = await GetBoxStatusAsync(supply, code).ConfigureAwait(false);

                if (isShip)
                {
                    // Отгружать можно только закрытый короб
                    if (string.Equals(status, "Открыт", StringComparison.OrdinalIgnoreCase))
                    {
                        await AddLogAsync(supply, "warn", "Сначала закройте короб", boxCode: code).ConfigureAwait(false);
                        return (null, "Сначала закройте короб");
                    }

                    await MarkBoxShippedAsync(supply, code).ConfigureAwait(false);
                    return (null, "Короб отгружен");
                }

                // Не отгрузка: открываем/закрываем при повторном скане
                if (!string.Equals(status, "Открыт", StringComparison.OrdinalIgnoreCase))
                {
                    await MarkBoxOpenedAsync(supply, code).ConfigureAwait(false);
                    // Вернём маркер, по которому UI поставит активный короб
                    return (null, $"Активен короб: {code}");
                }
                else
                {
                    await MarkBoxClosedAsync(supply, code).ConfigureAwait(false);
                    return (null, "Короб закрыт");
                }
            }

            // --------------------- Скан ТОВАРА ---------------------
            // Должен быть выбран активный короб => определяем склад
            var warehouse = ResolveWarehouseByBox(activeBoxCode);
            var itemsByBarcode = _db.SupplyItems
                .Where(i => i.SupplyId == supply.Id && i.Barcode == code)
                .ToList();

            if (warehouse == null)
            {
                await AddLogAsync(supply, "warn",
                    "Сначала отсканируйте короб, чтобы выбрать склад.",
                    barcode: code, boxCode: activeBoxCode).ConfigureAwait(false);

                return (itemsByBarcode.FirstOrDefault(), "Сначала отсканируйте короб, чтобы выбрать склад.");
            }

            // Ищем именно позицию этого склада
            var item = _db.SupplyItems.SingleOrDefault(i =>
                i.SupplyId == supply.Id && i.Barcode == code && i.Warehouse == warehouse);

            if (item == null)
            {
                await AddLogAsync(supply, "error",
                    "Товар не найден в плане текущего склада",
                    barcode: code, warehouse: warehouse, boxCode: activeBoxCode).ConfigureAwait(false);

                return (null, "Товар не найден в плане текущего склада");
            }

            // ----- Удаление -----
            if (isRemove)
            {
                // Сколько реально лежит ЭТОГО ШК в ЭТОМ активном коробе (по scan-логам)?
                var qtyInThisBox = _db.ScanLogs
                    .Where(l => l.SupplyId == supply.Id
                                && l.Kind == "scan"
                                && l.BoxCode == activeBoxCode
                                && l.Barcode == code)
                    .Sum(l => l.Delta);

                if (qtyInThisBox <= 0)
                {
                    await AddLogAsync(supply, "info", "В этом коробе такого товара нет",
                        barcode: code, warehouse: warehouse, boxCode: activeBoxCode).ConfigureAwait(false);

                    return (item, "В этом коробе такого товара нет");
                }

                // Корректно уменьшаем сборку по складу
                if (item.QtyPicked > 0) item.QtyPicked -= 1;
                if (item.QtyPicked < item.Need) item.Status = "Ожидает";

                await _db.SaveChangesAsync().ConfigureAwait(false);

                var remainDel = Math.Max(0, item.Need - item.QtyPicked);

                // В лог обязательно пишем delta:-1 и boxCode — это двигает содержимое «Короба»
                await AddLogAsync(supply, "scan",
                    $"Удалено из короба ({warehouse}). Осталось {remainDel}",
                    barcode: code, warehouse: warehouse, boxCode: activeBoxCode, delta: -1).ConfigureAwait(false);

                return (item, $"Удалено. Осталось {remainDel}");
            }

            // ----- Отгрузка товаром: подсказка -----
            if (isShip)
            {
                await AddLogAsync(supply, "warn",
                    "В режиме 'Отгрузка' сканируйте ШК коробов.",
                    barcode: code, warehouse: warehouse, boxCode: activeBoxCode).ConfigureAwait(false);

                return (item, "В режиме 'Отгрузка' сканируйте ШК коробов.");
            }

            // ----- Добавление -----
            if (item.QtyPicked >= item.Need)
            {
                await AddLogAsync(supply, "info",
                    "Для этого склада хватит",
                    barcode: code, warehouse: warehouse, boxCode: activeBoxCode).ConfigureAwait(false);

                await Speech.SayAsync("ХВАТИТ");
                return (item, "Для этого склада хватит");
            }

            item.QtyPicked += 1;
            if (item.QtyPicked >= item.Need) item.Status = "Собран";
            await _db.SaveChangesAsync().ConfigureAwait(false);

            var remain = Math.Max(0, item.Need - item.QtyPicked);

            await AddLogAsync(supply, "scan",
                $"+1 ({warehouse}). Осталось {remain}",
                barcode: code, warehouse: warehouse, boxCode: activeBoxCode, delta: +1).ConfigureAwait(false);

            return (item, $"Осталось {remain}");
        }








        /// <summary>
        /// Обёртка — некоторые страницы зовут «расширенный» метод. Делает то же, что ApplyScanAsync.
        /// </summary>
        public Task<(SupplyItem? item, string result)> ApplyScanAdvancedAsync(
            Supply supply, string code, string mode, string? activeBoxCode)
            => ApplyScanAsync(supply, code, mode, activeBoxCode);
        public Task<List<dynamic>> GetBoxTotalsAsync(Supply supply)
        {
            // --- Собрано по КОРОБУ и ШК из логов scan (+/-) ---
            var perBox = _db.ScanLogs
                .Where(l => l.SupplyId == supply.Id
                            && l.Kind == "scan"
                            && l.BoxCode != null
                            && l.Barcode != null)
                .GroupBy(l => new
                {
                    BoxCode = l.BoxCode,   // без операторов '!'
                    Barcode = l.Barcode
                })
                .Select(g => new
                {
                    BoxCode = g.Key.BoxCode!,
                    Barcode = g.Key.Barcode!,
                    Picked = g.Sum(x => x.Delta)
                })
                .Where(x => x.Picked > 0)
                .ToList();

            // --- Нужное всего по ШК (суммарно по всем складам этой поставки) ---
            var needs = _db.SupplyItems
                .Where(i => i.SupplyId == supply.Id)
                .GroupBy(i => i.Barcode)
                .Select(g => new
                {
                    Barcode = g.Key!,
                    NeedTotal = g.Sum(x => x.Need),
                    AnyItem = g.First()
                })
                .ToDictionary(x => x.Barcode, x => x);

            // --- Статус короба по последнему box/ship логу ---
            string StatusOf(string code)
            {
                var lastState = _db.ScanLogs
                    .Where(l => l.SupplyId == supply.Id
                                && l.BoxCode == code
                                && (l.Kind == "box" || l.Kind == "ship"))
                    .OrderByDescending(l => l.Ts)
                    .FirstOrDefault();

                if (lastState == null) return "—";
                if (string.Equals(lastState.Kind, "ship", StringComparison.OrdinalIgnoreCase)) return "Отгружен";

                var msg = lastState.Message ?? string.Empty;
                return msg.IndexOf("Закрыт", StringComparison.OrdinalIgnoreCase) >= 0 ? "Закрыт" : "Открыт";
            }

            // --- Сбор итоговых строк ---
            var rows = new List<dynamic>();
            foreach (var r in perBox)
            {
                needs.TryGetValue(r.Barcode, out var n);

                var name = n?.AnyItem?.Name ?? string.Empty;
                var article = n?.AnyItem?.Article ?? string.Empty;
                var need = n?.NeedTotal ?? 0;
                var picked = r.Picked;
                var remain = Math.Max(0, need - picked);

                dynamic d = new System.Dynamic.ExpandoObject();
                d.BoxCode = r.BoxCode;
                d.Name = name;
                d.Article = article;
                d.Barcode = r.Barcode;
                d.Picked = picked;
                d.Need = need;
                d.Remain = remain;
                d.Status = StatusOf(r.BoxCode);

                rows.Add(d);
            }

            // Если нужна сортировка — раскомментируйте:
            // rows = rows.OrderBy(x => (string)x.BoxCode).ThenBy(x => (string)x.Name).ToList<dynamic>();

            return Task.FromResult(rows);
        }




        // ---------------- Итоги ----------------

        public Task<object> GetTotalsAsync(Supply supply)
        {
            var rows = _db.SupplyItems.Where(i => i.SupplyId == supply.Id).ToList();

            var grouped = rows
                .GroupBy(r => new { r.Article, r.Barcode, r.Name })
                .Select(g =>
                {
                    int CFO = g.Where(x => x.Warehouse == "CFO").Sum(x => x.Need);
                    int Sam = g.Where(x => x.Warehouse == "Samara").Sum(x => x.Need);
                    int YUG = g.Where(x => x.Warehouse == "YUG").Sum(x => x.Need);

                    int picked = g.Sum(x => x.QtyPicked);
                    int total = CFO + Sam + YUG;
                    int remaining = Math.Max(0, total - picked);

                    string status = remaining == 0 ? "Собран" : picked > 0 ? "Собран частично" : "Ожидает";

                    dynamic d = new ExpandoObject();
                    d.Name = g.Key.Name;
                    d.Article = g.Key.Article;
                    d.Barcode = g.Key.Barcode;
                    d.CFO = CFO;
                    d.Samara = Sam;
                    d.YUG = YUG;
                    d.Total = total;
                    d.Picked = picked;
                    d.Remaining = remaining;
                    d.Status = status;
                    return d;
                })
                .OrderBy(d => d.Name)
                .ToList<dynamic>();

            return Task.FromResult<object>(grouped);
        }

        public async Task<object> GetTotalsScannedAsync(Supply supply)
        {
            var all = await GetTotalsAsync(supply).ConfigureAwait(false);
            var only = (all as IEnumerable<dynamic> ?? Array.Empty<dynamic>())
                .Where(d => (int)d.Picked > 0)
                .ToList();
            return only;
        }

        // ---------------- Логи ----------------

        public async Task AddLogAsync(Supply supply, string kind, string message,
                                      string? barcode = null, string? warehouse = null,
                                      string? boxCode = null, int delta = 0)
        {
            _db.ScanLogs.Add(new ScanLog
            {
                SupplyId = supply.Id,
                Ts = DateTime.Now,
                Kind = kind,
                Message = message,
                Barcode = barcode,
                Warehouse = warehouse,
                BoxCode = boxCode,
                Delta = delta
            });
            await _db.SaveChangesAsync().ConfigureAwait(false);
        }
    }
}

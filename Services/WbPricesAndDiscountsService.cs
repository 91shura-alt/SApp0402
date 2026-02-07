using Microsoft.EntityFrameworkCore;
using SellerOps.App.Data;
using SellerOps.App.Domain;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SellerOps.App.Services
{
    /// <summary>
    /// WB Prices & Discounts API (discounts-prices-api).
    /// Читает текущие цены/скидки и сохраняет офлайн в SQLite.
    /// </summary>
    public class WbPricesAndDiscountsService
    {
        private readonly AppDbContext _db;
        private readonly WbHttpClientFactory _http = new();

        public WbPricesAndDiscountsService(AppDbContext? db = null)
        {
            _db = db ?? AppDbContext.Instance;
        }

        private (string token, string baseUrl) GetCreds()
        {
            var localToken = AppSettings.Instance.EncryptedPricesAndDiscountsToken;
            if (!string.IsNullOrWhiteSpace(localToken))
            {
                var localTokenValue = SecureStorage.Unprotect(localToken)?.Trim();
                if (string.IsNullOrWhiteSpace(localTokenValue))
                    throw new InvalidOperationException("Локальный токен 'PricesAndDiscounts' пустой/не расшифровался. Открой WB настройки и сохрани токен заново.");

                var localBaseUrl = AppSettings.Instance.PricesAndDiscountsIsSandbox
                    ? "https://discounts-prices-api-sandbox.wildberries.ru"
                    : "https://discounts-prices-api.wildberries.ru";

                return (localTokenValue!, localBaseUrl);
            }

            var row = _db.ApiTokens.AsNoTracking()
                .OrderByDescending(x => x.Id)
                .FirstOrDefault(x => x.Category == "PricesAndDiscounts");

            if (row == null)
                throw new InvalidOperationException("Не найден токен категории 'PricesAndDiscounts'. Открой WB настройки и добавь токен.");

            if (!SecureStorage.TryUnprotect(row.EncryptedToken, out var token))
                throw new InvalidOperationException("Токен 'PricesAndDiscounts' был сохранён на другом ПК/пользователе. Пересохраните токен в настройках WB или включите режим хранения без шифрования.");

            token = token?.Trim();
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("Токен 'PricesAndDiscounts' пустой/не расшифровался.");

            // В проекте baseUrl уже показывается в UI (WbSettingsPage), но здесь делаем устойчиво.
            var baseUrl = row.IsSandbox
                ? "https://discounts-prices-api-sandbox.wildberries.ru"
                : "https://discounts-prices-api.wildberries.ru";

            return (token!, baseUrl);
        }

        private async Task SaveRawAsync(string kind, string fileName, string json, CancellationToken ct)
        {
            _db.WbRawFiles.Add(new WbRawFile
            {
                Kind = kind,
                FileName = fileName,
                CreatedAtUtc = DateTime.UtcNow,
                Json = json
            });
            await _db.SaveChangesAsync(ct);
        }

        /// <summary>
        /// Импорт всех товаров с ценами/скидками.
        /// Использует пагинацию limit/offset.
        /// </summary>
        public async Task<(int goods, int sizes)> ImportAllGoodsAsync(int limit = 1000, CancellationToken ct = default)
        {
            if (limit <= 0) limit = 1000;
            if (limit > 1000) limit = 1000; // по документации WB лимит не больше 1000

            var (token, baseUrl) = GetCreds();
            using var http = _http.Create(baseUrl, token, bearerHeader: false);

            int totalGoods = 0;
            int totalSizes = 0;

            // чтобы не убить UI – коммитим постранично
            for (int offset = 0, page = 1; page <= 100000; page++, offset += limit)
            {
                ct.ThrowIfCancellationRequested();

                // WB Discounts & Prices: список товаров
                // Актуальный путь: GET /api/v2/list/goods/filter
                // Старый /api/v2/list/goods у части аккаунтов/версий отдаёт 404.
                var url = $"/api/v2/list/goods/filter?limit={limit}&offset={offset}";
                using var resp = await http.GetAsync(url, ct);
                var payload = await resp.Content.ReadAsStringAsync(ct);

                await SaveRawAsync(
                    kind: "prices_list_goods",
                    fileName: $"prices_list_goods_p{page:00000}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json",
                    json: payload,
                    ct: ct);

                if (!resp.IsSuccessStatusCode)
                    throw new InvalidOperationException($"WB PricesAndDiscounts {url} вернул {(int)resp.StatusCode}. См. WbRawFiles/logs.");

                var listGoods = ExtractListGoods(payload);
                if (listGoods.Count == 0)
                    break;

                var importedAt = DateTime.UtcNow;

                // Быстрый upsert по nmID
                var nmIds = listGoods
                    .Select(x => TryGetInt64(x, "nmID") ?? TryGetInt64(x, "nmId") ?? 0)
                    .Where(x => x > 0)
                    .Distinct()
                    .ToList();

                var existingGoods = await _db.WbPriceGoods
                    .Where(x => nmIds.Contains(x.NmId))
                    .ToDictionaryAsync(x => x.NmId, ct);

                // удаляем старые размеры по этим nmIds одним запросом (EF Core 8 умеет ExecuteDeleteAsync)
                try
                {
                    await _db.WbPriceSizes
                        .Where(x => nmIds.Contains(x.NmId))
                        .ExecuteDeleteAsync(ct);
                }
                catch
                {
                    // fallback для провайдеров/версий, где ExecuteDeleteAsync недоступен
                    var old = await _db.WbPriceSizes.Where(x => nmIds.Contains(x.NmId)).ToListAsync(ct);
                    _db.WbPriceSizes.RemoveRange(old);
                }

                foreach (var g in listGoods)
                {
                    var nmId = TryGetInt64(g, "nmID") ?? TryGetInt64(g, "nmId") ?? 0;
                    if (nmId <= 0) continue;

                    var vendorCode = TryGetString(g, "vendorCode") ?? "";
                    var currency = TryGetInt32(g, "currencyIsoCode4217");
                    var discount = TryGetInt32(g, "discount");
                    var clubDiscount = TryGetInt32(g, "clubDiscount");
                    var editableSizePrice = TryGetBool(g, "editableSizePrice");
                    var isBadTurnover = TryGetBool(g, "isBadTurnover");

                    if (!existingGoods.TryGetValue(nmId, out var row))
                    {
                        row = new WbPriceGood
                        {
                            NmId = nmId,
                            VendorCode = vendorCode,
                            CurrencyIsoCode4217 = currency,
                            Discount = discount,
                            ClubDiscount = clubDiscount,
                            EditableSizePrice = editableSizePrice,
                            IsBadTurnover = isBadTurnover,
                            ImportedAtUtc = importedAt,
                            RawJson = g.GetRawText()
                        };
                        _db.WbPriceGoods.Add(row);
                        existingGoods[nmId] = row;
                    }
                    else
                    {
                        row.VendorCode = vendorCode;
                        row.CurrencyIsoCode4217 = currency;
                        row.Discount = discount;
                        row.ClubDiscount = clubDiscount;
                        row.EditableSizePrice = editableSizePrice;
                        row.IsBadTurnover = isBadTurnover;
                        row.ImportedAtUtc = importedAt;
                        row.RawJson = g.GetRawText();
                    }

                    totalGoods++;

                    // sizes[]
                    if (g.TryGetProperty("sizes", out var sizes) && sizes.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var s in sizes.EnumerateArray())
                        {
                            var sizeId = TryGetInt64(s, "sizeID") ?? TryGetInt64(s, "sizeId") ?? 0;
                            if (sizeId <= 0) continue;

                            var tech = TryGetString(s, "techSizeName") ?? "";
                            var price = TryGetDecimal(s, "price");
                            var discounted = TryGetDecimal(s, "discountedPrice");
                            var clubDiscounted = TryGetDecimal(s, "clubDiscountedPrice");

                            _db.WbPriceSizes.Add(new WbPriceSize
                            {
                                NmId = nmId,
                                SizeId = sizeId,
                                TechSizeName = tech,
                                Price = price,
                                DiscountedPrice = discounted,
                                ClubDiscountedPrice = clubDiscounted,
                                ImportedAtUtc = importedAt,
                                RawJson = s.GetRawText()
                            });
                            totalSizes++;
                        }
                    }
                }

                await _db.SaveChangesAsync(ct);

                // если пришло меньше лимита – следующей страницы нет
                if (listGoods.Count < limit)
                    break;
            }

            return (totalGoods, totalSizes);
        }

        /// <summary>
        /// Импорт товаров по списку nmID (до 100 шт за запрос) через POST /api/v2/list/goods/filter.\n
        /// Удобно для точечной/инкрементальной синхронизации.
        /// </summary>
        public async Task<(int goods, int sizes)> ImportGoodsByNmIdsAsync(IReadOnlyCollection<long> nmIds, CancellationToken ct = default)
        {
            if (nmIds == null || nmIds.Count == 0) return (0, 0);

            var (token, baseUrl) = GetCreds();
            using var http = _http.Create(baseUrl, token, bearerHeader: false);

            int totalGoods = 0;
            int totalSizes = 0;
            var importedAt = DateTime.UtcNow;

            // ВБ ограничивает список nmIDs (на момент документации – 100)
            var chunks = nmIds.Where(x => x > 0).Distinct().Chunk(100);
            foreach (var chunk in chunks)
            {
                ct.ThrowIfCancellationRequested();

                var body = JsonSerializer.Serialize(new { nmIDs = chunk });
                using var resp = await http.PostAsync(
                    "/api/v2/list/goods/filter",
                    new StringContent(body, Encoding.UTF8, "application/json"),
                    ct);

                var payload = await resp.Content.ReadAsStringAsync(ct);

                await SaveRawAsync(
                    kind: "prices_list_goods_filter",
                    fileName: $"prices_list_goods_filter_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json",
                    json: payload,
                    ct: ct);

                if (!resp.IsSuccessStatusCode)
                    throw new InvalidOperationException($"WB PricesAndDiscounts /api/v2/list/goods/filter вернул {(int)resp.StatusCode}. См. WbRawFiles/logs.");

                var listGoods = ExtractListGoods(payload);
                if (listGoods.Count == 0) continue;

                var existingGoods = await _db.WbPriceGoods
                    .Where(x => chunk.Contains(x.NmId))
                    .ToDictionaryAsync(x => x.NmId, ct);

                // чистим размеры по nmId из чанка
                try
                {
                    await _db.WbPriceSizes.Where(x => chunk.Contains(x.NmId)).ExecuteDeleteAsync(ct);
                }
                catch
                {
                    var old = await _db.WbPriceSizes.Where(x => chunk.Contains(x.NmId)).ToListAsync(ct);
                    _db.WbPriceSizes.RemoveRange(old);
                }

                foreach (var g in listGoods)
                {
                    var nmId = TryGetInt64(g, "nmID") ?? TryGetInt64(g, "nmId") ?? 0;
                    if (nmId <= 0) continue;

                    var vendorCode = TryGetString(g, "vendorCode") ?? "";
                    var currency = TryGetInt32(g, "currencyIsoCode4217");
                    var discount = TryGetInt32(g, "discount");
                    var clubDiscount = TryGetInt32(g, "clubDiscount");
                    var editableSizePrice = TryGetBool(g, "editableSizePrice");
                    var isBadTurnover = TryGetBool(g, "isBadTurnover");

                    if (!existingGoods.TryGetValue(nmId, out var row))
                    {
                        row = new WbPriceGood
                        {
                            NmId = nmId,
                            VendorCode = vendorCode,
                            CurrencyIsoCode4217 = currency,
                            Discount = discount,
                            ClubDiscount = clubDiscount,
                            EditableSizePrice = editableSizePrice,
                            IsBadTurnover = isBadTurnover,
                            ImportedAtUtc = importedAt,
                            RawJson = g.GetRawText()
                        };
                        _db.WbPriceGoods.Add(row);
                        existingGoods[nmId] = row;
                    }
                    else
                    {
                        row.VendorCode = vendorCode;
                        row.CurrencyIsoCode4217 = currency;
                        row.Discount = discount;
                        row.ClubDiscount = clubDiscount;
                        row.EditableSizePrice = editableSizePrice;
                        row.IsBadTurnover = isBadTurnover;
                        row.ImportedAtUtc = importedAt;
                        row.RawJson = g.GetRawText();
                    }

                    totalGoods++;

                    if (g.TryGetProperty("sizes", out var sizes) && sizes.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var s in sizes.EnumerateArray())
                        {
                            var sizeId = TryGetInt64(s, "sizeID") ?? TryGetInt64(s, "sizeId") ?? 0;
                            if (sizeId <= 0) continue;

                            _db.WbPriceSizes.Add(new WbPriceSize
                            {
                                NmId = nmId,
                                SizeId = sizeId,
                                TechSizeName = TryGetString(s, "techSizeName") ?? "",
                                Price = TryGetDecimal(s, "price"),
                                DiscountedPrice = TryGetDecimal(s, "discountedPrice"),
                                ClubDiscountedPrice = TryGetDecimal(s, "clubDiscountedPrice"),
                                ImportedAtUtc = importedAt,
                                RawJson = s.GetRawText()
                            });
                            totalSizes++;
                        }
                    }
                }

                await _db.SaveChangesAsync(ct);
            }

            return (totalGoods, totalSizes);
        }

        /// <summary>
        /// Импорт товаров в карантине (если метод доступен в вашем контуре).
        /// </summary>
        public async Task<int> ImportQuarantineAsync(CancellationToken ct = default)
        {
            var (token, baseUrl) = GetCreds();
            using var http = _http.Create(baseUrl, token, bearerHeader: false);

            using var resp = await http.GetAsync("/api/v2/quarantine/goods", ct);
            var payload = await resp.Content.ReadAsStringAsync(ct);

            await SaveRawAsync(
                kind: "prices_quarantine_goods",
                fileName: $"prices_quarantine_goods_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json",
                json: payload,
                ct: ct);

            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"WB PricesAndDiscounts /api/v2/quarantine/goods вернул {(int)resp.StatusCode}. См. WbRawFiles/logs.");

            var list = ExtractListGoods(payload); // структура обычно такая же: listGoods
            if (list.Count == 0) return 0;

            // простая стратегия: очищаем карантин и вставляем заново
            try
            {
                await _db.WbPriceQuarantineGoods.ExecuteDeleteAsync(ct);
            }
            catch
            {
                _db.WbPriceQuarantineGoods.RemoveRange(await _db.WbPriceQuarantineGoods.ToListAsync(ct));
            }

            var importedAt = DateTime.UtcNow;
            int count = 0;

            foreach (var g in list)
            {
                var nmId = TryGetInt64(g, "nmID") ?? TryGetInt64(g, "nmId") ?? 0;
                if (nmId <= 0) continue;

                _db.WbPriceQuarantineGoods.Add(new WbPriceQuarantineGood
                {
                    NmId = nmId,
                    VendorCode = TryGetString(g, "vendorCode") ?? "",
                    Reason = TryGetString(g, "reason") ?? TryGetString(g, "message") ?? "",
                    ImportedAtUtc = importedAt,
                    RawJson = g.GetRawText()
                });
                count++;
            }

            await _db.SaveChangesAsync(ct);
            return count;
        }

        // ---------------- parsing helpers ----------------

        private static List<JsonElement> ExtractListGoods(string payload)
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            // частые варианты: { data: { listGoods: [...] } } или { listGoods: [...] }
            var data = root.TryGetProperty("data", out var d) ? d : root;

            // ВАЖНО: JsonElement привязан к JsonDocument. Если вернуть элементы напрямую,
            // а JsonDocument будет Dispose(), то при дальнейшем доступе получим:
            // "Cannot access a disposed object. Object name: 'JsonDocument'."
            // Поэтому обязательно клонируем элементы.
            if (data.TryGetProperty("listGoods", out var lg) && lg.ValueKind == JsonValueKind.Array)
                return lg.EnumerateArray().Select(x => x.Clone()).ToList();

            // иногда список называется goods
            if (data.TryGetProperty("goods", out var g2) && g2.ValueKind == JsonValueKind.Array)
                return g2.EnumerateArray().Select(x => x.Clone()).ToList();

            return new List<JsonElement>();
        }

        private static string? TryGetString(JsonElement e, string name)
        {
            return e.TryGetProperty(name, out var p)
                ? (p.ValueKind == JsonValueKind.String ? p.GetString() : p.ToString())
                : null;
        }

        private static long? TryGetInt64(JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out var p)) return null;
            if (p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var v)) return v;
            if (p.ValueKind == JsonValueKind.String && long.TryParse(p.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var v2)) return v2;
            return null;
        }

        private static int? TryGetInt32(JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out var p)) return null;
            if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v)) return v;
            if (p.ValueKind == JsonValueKind.String && int.TryParse(p.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var v2)) return v2;
            return null;
        }

        private static bool? TryGetBool(JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out var p)) return null;
            if (p.ValueKind == JsonValueKind.True) return true;
            if (p.ValueKind == JsonValueKind.False) return false;
            if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v)) return v != 0;
            if (p.ValueKind == JsonValueKind.String)
            {
                var s = p.GetString();
                if (bool.TryParse(s, out var b)) return b;
                if (int.TryParse(s, out var i)) return i != 0;
            }
            return null;
        }

        private static decimal? TryGetDecimal(JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out var p)) return null;
            if (p.ValueKind == JsonValueKind.Number)
            {
                if (p.TryGetDecimal(out var d)) return d;
                if (p.TryGetDouble(out var dd)) return (decimal)dd;
            }
            if (p.ValueKind == JsonValueKind.String &&
                decimal.TryParse(p.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var ds))
                return ds;
            return null;
        }
    }
}

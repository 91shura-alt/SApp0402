using Microsoft.EntityFrameworkCore;
using SellerOps.App.Data;
using SellerOps.App.Domain;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SellerOps.App.Services
{
    /// <summary>
    /// WB Marketplace API (FBS сборочные задания)
    /// Токен берём из ApiTokens.Category == "Marketplace"
    /// </summary>
    public class WbMarketplaceService
    {
        private readonly AppDbContext _db;
        private readonly WbHttpClientFactory _http;

        public WbMarketplaceService(AppDbContext? db = null, WbHttpClientFactory? http = null)
        {
            _db = db ?? AppDbContext.Instance;
            _http = http ?? new WbHttpClientFactory();
        }

        private (string token, string baseUrl) GetCreds(string category)
        {
            if (category == "Marketplace" && !string.IsNullOrWhiteSpace(AppSettings.Instance.EncryptedMarketplaceToken))
            {
                var token = SecureStorage.Unprotect(AppSettings.Instance.EncryptedMarketplaceToken)?.Trim();
                if (string.IsNullOrWhiteSpace(token))
                    throw new InvalidOperationException("Локальный токен 'Marketplace' пустой/не расшифровался. Открой WB настройки и сохрани токен заново.");

                var baseUrl = AppSettings.Instance.MarketplaceIsSandbox
                    ? "https://marketplace-api-sandbox.wildberries.ru"
                    : "https://marketplace-api.wildberries.ru";

                return (token!, baseUrl);
            }

            var row = _db.ApiTokens.AsNoTracking()
                .OrderByDescending(x => x.Id)
                .FirstOrDefault(x => x.Category == category);

            if (row == null)
                throw new InvalidOperationException($"Не найден токен для категории '{category}'. Добавь токен в настройках WB.");

            if (!SecureStorage.TryUnprotect(row.EncryptedToken, out var token))
                throw new InvalidOperationException("Токен Marketplace был сохранён на другом ПК/пользователе. Пересохраните токен в настройках WB или включите режим хранения без шифрования.");

            token = token?.Trim();
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException($"Токен категории '{category}' пустой/не расшифровался.");

            var baseUrl = row.IsSandbox
                ? "https://marketplace-api-sandbox.wildberries.ru"
                : "https://marketplace-api.wildberries.ru";

            return (token!, baseUrl);
        }

        public async Task<int> ImportNewFbsOrdersAsync(CancellationToken ct = default)
        {
            var (token, baseUrl) = GetCreds("Marketplace");
            using var http = _http.Create(baseUrl, token, bearerHeader: false);

            var url = $"{baseUrl}/api/v3/orders/new";
            using var resp = await http.GetAsync(url, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                SafeLog($"WB marketplace orders/new error {resp.StatusCode}\nURL: {url}\nBODY:\n{body}");
                throw new InvalidOperationException($"WB Marketplace orders/new вернул {resp.StatusCode}. См. logs.");
            }

            await SaveRawAsync("marketplace_fbs_orders_new",
                $"fbs_orders_new_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json", body, ct);

            var parsed = ParseOrders(body);
            var added = await UpsertOrdersAsync(parsed, ct);

            // подтягиваем статусы для всех затронутых заказов
            await UpdateStatusesAsync(http, parsed.Select(x => x.OrderId).Distinct().ToList(), ct);

            return added;
        }

        public async Task<int> ImportFbsOrdersByPeriodAsync(DateTime dateFromUtc, DateTime dateToUtc, int pageSize = 1000, CancellationToken ct = default)
        {
            if (pageSize < 1 || pageSize > 1000) pageSize = 1000;
            if (dateToUtc < dateFromUtc) (dateFromUtc, dateToUtc) = (dateToUtc, dateFromUtc);

            var (token, baseUrl) = GetCreds("Marketplace");
            using var http = _http.Create(baseUrl, token, bearerHeader: false);

            var from = new DateTimeOffset(dateFromUtc.ToUniversalTime()).ToUnixTimeSeconds();
            var to = new DateTimeOffset(dateToUtc.ToUniversalTime()).ToUnixTimeSeconds();

            long next = 0;
            var totalAdded = 0;
            var page = 0;

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                page++;

                var url = $"{baseUrl}/api/v3/orders?limit={pageSize}&next={next}&dateFrom={from}&dateTo={to}";
                using var resp = await http.GetAsync(url, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);

                if (!resp.IsSuccessStatusCode)
                {
                    SafeLog($"WB marketplace orders(period) error {resp.StatusCode}\nURL: {url}\nBODY:\n{body}");
                    throw new InvalidOperationException($"WB Marketplace orders(period) вернул {resp.StatusCode}. См. logs.");
                }

                await SaveRawAsync("marketplace_fbs_orders",
                    $"marketplace_fbs_orders_{dateFromUtc:yyyyMMdd}_{dateToUtc:yyyyMMdd}_p{page:000}_{DateTime.UtcNow:HHmmss}.json",
                    body, ct);

                var parsed = ParseOrders(body);
                if (parsed.Count == 0) break;

                totalAdded += await UpsertOrdersAsync(parsed, ct);

                await UpdateStatusesAsync(http, parsed.Select(x => x.OrderId).Distinct().ToList(), ct);

                var newNext = TryReadNext(body);
                if (newNext == null || newNext.Value == next) break;
                next = newNext.Value;
            }

            return totalAdded;
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

        private async Task<int> UpsertOrdersAsync(List<WbFbsOrder> orders, CancellationToken ct)
        {
            orders = orders
                .GroupBy(x => x.OrderId)
                .Select(g => g.First())
                .ToList();

            if (orders.Count == 0) return 0;

            var ids = orders.Select(x => x.OrderId).Distinct().ToArray();

            var existing = await _db.WbFbsOrders
                .Where(x => ids.Contains(x.OrderId))
                .ToListAsync(ct);

            var map = existing.ToDictionary(x => x.OrderId);
            var added = 0;

            foreach (var o in orders)
            {
                if (map.TryGetValue(o.OrderId, out var cur))
                {
                    cur.OrderUid = o.OrderUid;
                    cur.Rid = o.Rid;
                    cur.AddressFull = o.AddressFull;
                    cur.SupplyId = o.SupplyId;
                    cur.CreatedAt = o.CreatedAt;
                    cur.Article = o.Article;
                    cur.NmId = o.NmId;
                    cur.ChrtId = o.ChrtId;
                    cur.WarehouseId = o.WarehouseId;
                    cur.OfficeId = o.OfficeId;

                    cur.Price = o.Price;
                    cur.FinalPrice = o.FinalPrice;
                    cur.CurrencyCode = o.CurrencyCode;

                    cur.ConvertedPrice = o.ConvertedPrice;
                    cur.ConvertedFinalPrice = o.ConvertedFinalPrice;
                    cur.ConvertedCurrencyCode = o.ConvertedCurrencyCode;

                    cur.CargoType = o.CargoType;
                    cur.IsZeroOrder = o.IsZeroOrder;

                    cur.SalePrice = o.SalePrice;
                    cur.ScanPrice = o.ScanPrice;

                    cur.Ddate = o.Ddate;
                    cur.SellerDate = o.SellerDate;
                    cur.ColorCode = o.ColorCode;

                    cur.AddressFull = o.AddressFull;
                    cur.AddressLatitude = o.AddressLatitude;
                    cur.AddressLongitude = o.AddressLongitude;

                    cur.OfficesJson = o.OfficesJson;
                    cur.SkusJson = o.SkusJson;

                    cur.RequiredMetaJson = o.RequiredMetaJson;
                    cur.OptionalMetaJson = o.OptionalMetaJson;
                    cur.IsB2B = o.IsB2B;

                    cur.DeliveryType = o.DeliveryType;
                    cur.Comment = o.Comment;
                    cur.RawJson = o.RawJson;

                    cur.Status = o.Status; // если WB отдаёт status/state
                    cur.ImportedAtUtc = DateTime.UtcNow;
                }
                else
                {
                    o.ImportedAtUtc = DateTime.UtcNow;
                    _db.WbFbsOrders.Add(o);
                    added++;
                }
            }

            await _db.SaveChangesAsync(ct);
            return added;
        }

        private async Task UpdateStatusesAsync(HttpClient http, List<long> orderIds, CancellationToken ct)
        {
            if (orderIds == null || orderIds.Count == 0) return;

            // WB: до 100 заказов за запрос
            const int chunk = 100;

            for (int i = 0; i < orderIds.Count; i += chunk)
            {
                ct.ThrowIfCancellationRequested();

                var part = orderIds.Skip(i).Take(chunk).ToArray();
                var reqJson = JsonSerializer.Serialize(new { orders = part });

                using var resp = await http.PostAsync("/api/v3/orders/status",
                    new StringContent(reqJson, Encoding.UTF8, "application/json"), ct);

                var body = await resp.Content.ReadAsStringAsync(ct);

                if (!resp.IsSuccessStatusCode)
                {
                    // статус — не критично, но логируем
                    SafeLog($"WB marketplace orders/status error {resp.StatusCode}\nBODY:\n{body}");
                    continue;
                }

                // сохраним raw тоже
                await SaveRawAsync("marketplace_fbs_orders_status",
                    $"marketplace_fbs_orders_status_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{i / chunk:000}.json",
                    body, ct);

                // парсим максимально мягко
                var map = new Dictionary<long, (string? supplier, string? wb)>();

                try
                {
                    using var doc = JsonDocument.Parse(body);
                    JsonElement arr;

                    if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("orders", out var o) && o.ValueKind == JsonValueKind.Array)
                        arr = o;
                    else if (doc.RootElement.ValueKind == JsonValueKind.Array)
                        arr = doc.RootElement;
                    else
                        continue;

                    foreach (var el in arr.EnumerateArray())
                    {
                        var id = GetInt64(el, "id") ?? GetInt64(el, "orderId");
                        if (id == null) continue;

                        var supplierStatus = GetString(el, "supplierStatus");
                        var wbStatus = GetString(el, "wbStatus");

                        map[id.Value] = (supplierStatus, wbStatus);
                    }
                }
                catch
                {
                    continue;
                }

                if (map.Count == 0) continue;

                var ids = map.Keys.ToArray();
                var rows = await _db.WbFbsOrders.Where(x => ids.Contains(x.OrderId)).ToListAsync(ct);

                foreach (var r in rows)
                {
                    if (map.TryGetValue(r.OrderId, out var st))
                    {
                        r.SupplierStatus = st.supplier;
                        r.WbStatus = st.wb;
                        r.ImportedAtUtc = DateTime.UtcNow;
                    }
                }

                await _db.SaveChangesAsync(ct);
            }
        }

        private static List<WbFbsOrder> ParseOrders(string json)
        {
            var result = new List<WbFbsOrder>();
            if (string.IsNullOrWhiteSpace(json)) return result;

            try
            {
                using var doc = JsonDocument.Parse(json);

                JsonElement ordersEl;
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("orders", out var o1) && o1.ValueKind == JsonValueKind.Array)
                    ordersEl = o1;
                else if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("data", out var o2) && o2.ValueKind == JsonValueKind.Array)
                    ordersEl = o2;
                else if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    ordersEl = doc.RootElement;
                else
                    return result;

                foreach (var el in ordersEl.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.Object) continue;

                    var id = GetInt64(el, "id");
                    if (id == null) continue;

                    var order = new WbFbsOrder
                    {
                        OrderId = id.Value,

                        OrderUid = GetString(el, "orderUid"),
                        Rid = GetString(el, "rid"),
                        SupplyId = GetString(el, "supplyId") ?? GetString(el, "supplyID"),
                        AddressFull =
    GetNestedString(el, "deliveryAddress", "fullAddress") ??
    GetNestedString(el, "deliveryAddress", "address") ??
    GetString(el, "addressFull") ??
    GetString(el, "address"),

                        CreatedAt = GetDateTimeUtc(el, "createdAt"),

                        Article = GetString(el, "article"),
                        NmId = GetInt64(el, "nmId"),
                        ChrtId = GetInt64(el, "chrtId"),
                        WarehouseId = GetInt64(el, "warehouseId"),
                        OfficeId = GetInt64(el, "officeId"),

                        Price = GetInt32(el, "price"),
                        FinalPrice = GetInt32(el, "finalPrice"),
                        CurrencyCode = GetInt32(el, "currencyCode"),

                        ConvertedPrice = GetInt32(el, "convertedPrice"),
                        ConvertedFinalPrice = GetInt32(el, "convertedFinalPrice"),
                        ConvertedCurrencyCode = GetInt32(el, "convertedCurrencyCode"),

                        CargoType = GetInt32(el, "cargoType"),
                        IsZeroOrder = GetBool(el, "isZeroOrder"),

                        SalePrice = GetInt32(el, "salePrice"),
                        ScanPrice = GetInt32(el, "scanPrice"),

                        Ddate = GetDateOnly(el, "ddate"),
                        SellerDate = GetDateOnly(el, "sellerDate"),

                        ColorCode = GetString(el, "colorCode"),

                        DeliveryType = GetString(el, "deliveryType"),
                        Comment = GetString(el, "comment"),

                        Status = GetString(el, "status") ?? GetString(el, "state"),

                        OfficesJson = GetArrayRaw(el, "offices"),
                        SkusJson = GetArrayRaw(el, "skus"),

                        RequiredMetaJson = GetAnyRaw(el, "requiredMeta"),
                        OptionalMetaJson = GetAnyRaw(el, "optionalMeta"),
                        IsB2B = GetBool(el, "isB2B"),

                        RawJson = el.GetRawText(),
                        ImportedAtUtc = DateTime.UtcNow
                    };

                    // address
                    if (el.TryGetProperty("address", out var addr) && addr.ValueKind == JsonValueKind.Object)
                    {
                        order.AddressFull = GetString(addr, "fullAddress") ?? GetString(addr, "address");
                        order.AddressLatitude = GetDouble(addr, "latitude");
                        order.AddressLongitude = GetDouble(addr, "longitude");
                    }

                    result.Add(order);
                }
            }
            catch { }

            return result;
        }

        private static long? TryReadNext(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("next", out var nextEl))
                {
                    if (nextEl.ValueKind == JsonValueKind.Number && nextEl.TryGetInt64(out var n))
                        return n;
                    if (nextEl.ValueKind == JsonValueKind.String && long.TryParse(nextEl.GetString(), out var ns))
                        return ns;
                }
            }
            catch { }

            return null;
        }

        private static string? GetString(JsonElement obj, string prop)
        {
            if (!obj.TryGetProperty(prop, out var el)) return null;
            if (el.ValueKind == JsonValueKind.String) return el.GetString();
            if (el.ValueKind == JsonValueKind.Number) return el.GetRawText();
            return null;
        }

        private static int? GetInt32(JsonElement obj, string prop)
        {
            if (!obj.TryGetProperty(prop, out var el)) return null;
            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var v)) return v;
            if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out var vs)) return vs;
            return null;
        }

        private static long? GetInt64(JsonElement obj, string prop)
        {
            if (!obj.TryGetProperty(prop, out var el)) return null;
            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var v)) return v;
            if (el.ValueKind == JsonValueKind.String && long.TryParse(el.GetString(), out var vs)) return vs;
            return null;
        }

        private static double? GetDouble(JsonElement obj, string prop)
        {
            if (!obj.TryGetProperty(prop, out var el)) return null;
            if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var v)) return v;
            if (el.ValueKind == JsonValueKind.String &&
                double.TryParse(el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var vs)) return vs;
            return null;
        }

        private static bool? GetBool(JsonElement obj, string prop)
        {
            if (!obj.TryGetProperty(prop, out var el)) return null;
            if (el.ValueKind == JsonValueKind.True) return true;
            if (el.ValueKind == JsonValueKind.False) return false;
            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var i)) return i != 0;
            if (el.ValueKind == JsonValueKind.String)
            {
                var s = el.GetString();
                if (bool.TryParse(s, out var b)) return b;
                if (int.TryParse(s, out var i2)) return i2 != 0;
            }
            return null;
        }

        private static DateTime? GetDateTimeUtc(JsonElement obj, string prop)
        {
            if (!obj.TryGetProperty(prop, out var el)) return null;
            if (el.ValueKind != JsonValueKind.String) return null;

            var s = el.GetString();
            if (string.IsNullOrWhiteSpace(s)) return null;

            if (DateTimeOffset.TryParse(s, out var dto))
                return dto.UtcDateTime;

            if (DateTime.TryParse(s, out var dt))
                return DateTime.SpecifyKind(dt, DateTimeKind.Utc);

            return null;
        }

        private static DateTime? GetDateOnly(JsonElement obj, string prop)
        {
            var s = GetString(obj, prop);
            if (string.IsNullOrWhiteSpace(s)) return null;

            // WB часто отдаёт dd.MM.yyyy
            if (DateTime.TryParseExact(s, "dd.MM.yyyy", CultureInfo.GetCultureInfo("ru-RU"),
                DateTimeStyles.None, out var dt))
                return dt.Date;

            if (DateTime.TryParse(s, out dt))
                return dt.Date;

            return null;
        }

        private static string? GetArrayRaw(JsonElement obj, string prop)
        {
            if (!obj.TryGetProperty(prop, out var el)) return null;
            return el.ValueKind == JsonValueKind.Array ? el.GetRawText() : null;
        }

        private static string? GetAnyRaw(JsonElement obj, string prop)
        {
            if (!obj.TryGetProperty(prop, out var el)) return null;
            if (el.ValueKind == JsonValueKind.Undefined || el.ValueKind == JsonValueKind.Null) return null;
            return el.GetRawText();
        }

        private static void SafeLog(string text)
        {
            try
            {
                var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, $"wb-marketplace-diagnostic-{DateTime.UtcNow:yyyyMMddHHmmss}.txt");
                File.WriteAllText(file, text, Encoding.UTF8);
            }
            catch { }
        }
        private static string? GetNestedString(JsonElement obj, string parent, string child)
        {
            if (!obj.TryGetProperty(parent, out var p) || p.ValueKind != JsonValueKind.Object) return null;
            return GetString(p, child);
        }

    }
}

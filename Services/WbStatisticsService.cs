using Microsoft.EntityFrameworkCore;
using SellerOps.App.Data;
using SellerOps.App.Domain;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.Net.Http;

namespace SellerOps.App.Services
{
    public class WbStatisticsService
    {
        private const int Max429Retries = 5;
        private static readonly TimeSpan MinRequestInterval = TimeSpan.FromMilliseconds(350);

        private readonly AppDbContext _db;
        private readonly WbHttpClientFactory _http = new();

        public WbStatisticsService(AppDbContext? db = null)
        {
            _db = db ?? AppDbContext.Instance;
        }

        private (string token, string baseUrl) GetCreds(string kind)
        {
            var row = _db.ApiTokens.AsNoTracking()
                .OrderByDescending(x => x.Id)
                .FirstOrDefault(x => x.Category == kind);

            if (row == null)
                throw new InvalidOperationException($"Не найден токен категории '{kind}'. Открой настройки и добавь токен.");

            var token = (SecureStorage.Unprotect(row.EncryptedToken) ?? "").Trim();
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException($"Токен категории '{kind}' пустой/не расшифровался. Открой WB настройки и добавь токен заново.");

            var baseUrl = row.IsSandbox
                ? "https://statistics-api-sandbox.wildberries.ru"
                : "https://statistics-api.wildberries.ru";

            return (token, baseUrl);
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

        // ---------------- REALIZATIONS ----------------

        public async Task<int> ImportRealizationByPeriodAsync(DateTime from, DateTime to, string reason, CancellationToken ct = default)
        {
            if (to < from) (from, to) = (to, from);

            var totalSaved = 0;

            for (var day = from.Date; day <= to.Date; day = day.AddDays(1))
            {
                ct.ThrowIfCancellationRequested();
                totalSaved += await ImportRealizationForDayAsync(day, ct);
            }

            return totalSaved;
        }

        private async Task<int> ImportRealizationForDayAsync(DateTime day, CancellationToken ct)
        {
            var (token, baseUrl) = GetCreds("Statistics");
            using var http = _http.Create(baseUrl, token, bearerHeader: false);

            long maxRrdId = _db.WbRealizationLines
                .Where(x => x.RrDt == day.Date)
                .Select(x => (long?)x.RrdId)
                .Max() ?? 0;

            var saved = 0;
            var isComplete = false;
            var page = 0;

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                page++;

                var url =
                    $"{baseUrl}/api/v5/supplier/reportDetailByPeriod" +
                    $"?dateFrom={day:yyyy-MM-dd}" +
                    $"&dateTo={day:yyyy-MM-dd}" +
                    $"&rrdid={maxRrdId}";

                var body = await GetWithRetryRawAsync(
                    http,
                    url,
                    "statistics_realizations",
                    $"statistics_realizations_{day:yyyyMMdd}_rrdid{maxRrdId}_p{page:000}",
                    ct);

                if (string.IsNullOrWhiteSpace(body) || body.Trim() == "[]")
                {
                    isComplete = true;
                    break;
                }

                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                {
                    isComplete = true;
                    break;
                }

                // собираем rrd_id пачкой
                var rrdIds = new List<long>();
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    var rrdId = GetInt64Any(el, "rrd_id") ?? 0;
                    if (rrdId > 0) rrdIds.Add(rrdId);
                }

                if (rrdIds.Count == 0) break;

                var existing = await _db.WbRealizationLines
                    .Where(x => rrdIds.Contains(x.RrdId))
                    .Select(x => x.RrdId)
                    .ToListAsync(ct);

                var existingSet = existing.ToHashSet();

                var batch = new List<WbRealizationLine>();

                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    var rrdId = GetInt64Any(el, "rrd_id") ?? 0;
                    if (rrdId <= 0) continue;
                    if (existingSet.Contains(rrdId)) continue;

                    var line = new WbRealizationLine
                    {
                        RrdId = rrdId,
                        RrDt = ParseNullableDateAny(el, "rr_dt"),
                        NmId = (long)(GetInt64Any(el, "nm_id") ?? 0),

                        SupplierArticle = GetStringAny(el, "sa_name") ?? "",
                        TechSize = GetStringAny(el, "ts_name") ?? "",
                        Barcode = GetStringAny(el, "barcode") ?? "",

                        WarehouseName = GetStringAny(el, "warehouse_name") ?? "",
                        Srid = GetStringAny(el, "srid") ?? "",

                        DocTypeName = GetStringAny(el, "doc_type_name") ?? "",
                        SupplierOperName = GetStringAny(el, "supplier_oper_name") ?? "",

                        Quantity = GetInt32Any(el, "quantity") ?? 0,

                        PriceWithDiscRub = GetDecimalAny(el, "price_with_disc") ?? 0m,
                        PpvzForPay = GetDecimalAny(el, "ppvz_for_pay") ?? 0m,
                        PpvzSalesCommission = GetDecimalAny(el, "ppvz_sales_commission") ?? 0m,

                        DeliveryRub = GetDecimalAny(el, "delivery_rub") ?? 0m,
                        StorageFee = GetDecimalAny(el, "storage_fee") ?? 0m,
                        Deduction = GetDecimalAny(el, "deduction") ?? 0m,
                        Penalty = GetDecimalAny(el, "penalty") ?? 0m,

                        CreateDt = ParseNullableDateAny(el, "create_dt"),
                        CancelDt = ParseNullableDateAny(el, "cancel_dt"),

                        RawJson = el.GetRawText()
                    };

                    batch.Add(line);
                    if (rrdId > maxRrdId) maxRrdId = rrdId;
                }

                if (batch.Count == 0) break;

                _db.WbRealizationLines.AddRange(batch);
                saved += batch.Count;
                await _db.SaveChangesAsync(ct);

                await Task.Delay(MinRequestInterval, ct);
            }

            UpsertImportLog("realizations", day, saved, maxRrdId, isComplete);
            await _db.SaveChangesAsync(ct);

            return saved;
        }

        // ---------------- STOCKS SNAPSHOT ----------------

        public async Task<int> SnapshotStocksAsync(DateTime? snapshotAt = null, CancellationToken ct = default)
        {
            var at = (snapshotAt ?? DateTime.UtcNow).ToUniversalTime();

            var (token, baseUrl) = GetCreds("Statistics");
            using var http = _http.Create(baseUrl, token, bearerHeader: false);

            var url = $"{baseUrl}/api/v1/supplier/stocks?dateFrom={at:yyyy-MM-ddTHH:mm:ssZ}";
            var body = await GetWithRetryRawAsync(
                http,
                url,
                "statistics_stocks",
                $"statistics_stocks_{at:yyyyMMdd_HHmmss}",
                ct);

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return 0;

            var rows = new List<WbStockSnapshot>();

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var qty = GetInt32Any(el, "quantity") ?? 0;

                var row = new WbStockSnapshot
                {
                    SnapshotAt = at,
                    Warehouse = GetStringAny(el, "warehouseName", "warehouse") ?? "",
                    NmId = (long)(GetInt64Any(el, "nmId") ?? 0),
                    Barcode = GetStringAny(el, "barcode") ?? "",
                    Quantity = qty,

                    SupplierArticle = GetStringAny(el, "supplierArticle", "supplier_article"),
                    TechSize = GetStringAny(el, "techSize", "techSizeName"),
                    Subject = GetStringAny(el, "subject"),
                    Category = GetStringAny(el, "category"),
                    Brand = GetStringAny(el, "brand"),

                    Price = GetDecimalAny(el, "price"),
                    Discount = GetInt32Any(el, "discount"),

                    QuantityFull = GetInt32Any(el, "quantityFull") ?? qty,
                    InWayToClient = GetInt32Any(el, "inWayToClient"),
                    InWayFromClient = GetInt32Any(el, "inWayFromClient"),

                    IsSupply = GetBoolAny(el, "isSupply"),
                    IsRealization = GetBoolAny(el, "isRealization"),

                    Code = GetStringAny(el, "code"),
                    SCCode = GetStringAny(el, "SCCode", "scCode"),
                    LastChangeDate = ParseNullableDateAny(el, "lastChangeDate"),

                    RawJson = el.GetRawText()
                };

                rows.Add(row);
            }

            if (rows.Count == 0) return 0;

            _db.WbStockSnapshots.AddRange(rows);
            await _db.SaveChangesAsync(ct);
            return rows.Count;
        }

        public async Task<int> ImportAdditionalStatisticsReportsByPeriodAsync(DateTime from, DateTime to, CancellationToken ct = default)
        {
            if (to < from) (from, to) = (to, from);

            var (token, baseUrl) = GetCreds("Statistics");
            using var http = _http.Create(baseUrl, token, bearerHeader: false);

            var reports = new (string kind, string endpoint)[]
            {
                ("statistics_orders", "/api/v1/supplier/orders"),
                ("statistics_sales", "/api/v1/supplier/sales"),
                ("statistics_incomes", "/api/v1/supplier/incomes"),
            };

            var saved = 0;
            foreach (var report in reports)
            {
                ct.ThrowIfCancellationRequested();

                var url = $"{baseUrl}{report.endpoint}?dateFrom={from:yyyy-MM-dd}";
                var body = await GetWithRetryRawAsync(
                    http,
                    url,
                    report.kind,
                    $"{report.kind}_{from:yyyyMMdd}_{to:yyyyMMdd}",
                    ct);

                UpsertImportLog(report.kind, to.Date, ParseArrayLen(body), 0, true);
                await _db.SaveChangesAsync(ct);
                saved += ParseArrayLen(body);

                await Task.Delay(MinRequestInterval, ct);
            }

            return saved;
        }

        // ---------------- IMPORT LOG ----------------

        private void UpsertImportLog(string kind, DateTime day, int addedRows, long maxRrdId, bool isComplete)
        {
            var row = _db.WbImportLogs
                .Where(x => x.Kind == kind && x.Day == day.Date)
                .OrderByDescending(x => x.Id)
                .FirstOrDefault();

            if (row == null)
            {
                row = new WbImportLog { Kind = kind, Day = day.Date };
                _db.WbImportLogs.Add(row);
            }

            row.ImportedAtUtc = DateTime.UtcNow;
            row.IsComplete = isComplete;
            row.AddedRows = addedRows;
            row.MaxRrdId = maxRrdId;
        }

        private async Task<string> GetWithRetryRawAsync(HttpClient http, string url, string rawKind, string filePrefix, CancellationToken ct)
        {
            for (var attempt = 1; attempt <= Max429Retries; attempt++)
            {
                using var resp = await http.GetAsync(url, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);

                await SaveRawAsync(rawKind,
                    $"{filePrefix}_try{attempt}_{DateTime.UtcNow:HHmmss}.json",
                    body,
                    ct);

                if (resp.IsSuccessStatusCode)
                    return body;

                if (resp.StatusCode == HttpStatusCode.TooManyRequests && attempt < Max429Retries)
                {
                    var delay = GetRetryDelay(resp, attempt);
                    await Task.Delay(delay, ct);
                    continue;
                }

                var bodySnippet = string.IsNullOrWhiteSpace(body)
                    ? "(пустой ответ)"
                    : body.Length > 300 ? body.Substring(0, 300) + "..." : body;

                throw new InvalidOperationException(
                    $"WB Statistics временно ограничил запросы ({(int)resp.StatusCode} {resp.StatusCode}). " +
                    $"Endpoint: {url}. Попробуйте уменьшить диапазон дат или повторить позже. " +
                    $"Детали ответа: {bodySnippet}. См. WbRawFiles/logs.");
            }

            throw new InvalidOperationException("WB Statistics: превышено число повторных попыток при 429 TooManyRequests.");
        }

        private static TimeSpan GetRetryDelay(HttpResponseMessage resp, int attempt)
        {
            var retryAfter = resp.Headers.RetryAfter;
            if (retryAfter?.Delta is TimeSpan delta && delta > TimeSpan.Zero)
                return delta;

            if (retryAfter?.Date is DateTimeOffset date)
            {
                var value = date - DateTimeOffset.UtcNow;
                if (value > TimeSpan.Zero) return value;
            }

            var seconds = Math.Min(30, (int)Math.Pow(2, attempt));
            return TimeSpan.FromSeconds(seconds);
        }

        private static int ParseArrayLen(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return 0;
            try
            {
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.ValueKind == JsonValueKind.Array
                    ? doc.RootElement.GetArrayLength()
                    : 0;
            }
            catch
            {
                return 0;
            }
        }

        // ---------------- helpers ----------------

        private static string? GetStringAny(JsonElement el, params string[] names)
        {
            foreach (var name in names)
            {
                if (el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String)
                    return p.GetString();
            }
            return null;
        }

        private static long? GetInt64Any(JsonElement el, params string[] names)
        {
            foreach (var name in names)
            {
                if (!el.TryGetProperty(name, out var p)) continue;

                if (p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var v)) return v;
                if (p.ValueKind == JsonValueKind.String && long.TryParse(p.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var vs)) return vs;
            }
            return null;
        }

        private static int? GetInt32Any(JsonElement el, params string[] names)
        {
            foreach (var name in names)
            {
                if (!el.TryGetProperty(name, out var p)) continue;

                if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v)) return v;
                if (p.ValueKind == JsonValueKind.String && int.TryParse(p.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var vs)) return vs;
            }
            return null;
        }

        private static decimal? GetDecimalAny(JsonElement el, params string[] names)
        {
            foreach (var name in names)
            {
                if (!el.TryGetProperty(name, out var p)) continue;

                if (p.ValueKind == JsonValueKind.Number && p.TryGetDecimal(out var v)) return v;
                if (p.ValueKind == JsonValueKind.String)
                {
                    var s = p.GetString();
                    if (decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v1)) return v1;
                    if (decimal.TryParse(s, NumberStyles.Any, CultureInfo.GetCultureInfo("ru-RU"), out var v2)) return v2;
                }
            }
            return null;
        }

        private static bool? GetBoolAny(JsonElement el, params string[] names)
        {
            foreach (var name in names)
            {
                if (!el.TryGetProperty(name, out var p)) continue;
                if (p.ValueKind == JsonValueKind.True) return true;
                if (p.ValueKind == JsonValueKind.False) return false;

                if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var vi)) return vi != 0;
                if (p.ValueKind == JsonValueKind.String)
                {
                    var s = p.GetString();
                    if (bool.TryParse(s, out var vb)) return vb;
                    if (int.TryParse(s, out var vi2)) return vi2 != 0;
                }
            }
            return null;
        }

        private static DateTime? ParseNullableDateAny(JsonElement el, params string[] names)
        {
            foreach (var name in names)
            {
                if (!el.TryGetProperty(name, out var p)) continue;
                if (p.ValueKind == JsonValueKind.Null) return null;

                if (p.ValueKind == JsonValueKind.String)
                {
                    var s = p.GetString();
                    if (string.IsNullOrWhiteSpace(s)) return null;

                    if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt)) return dt.ToUniversalTime();
                    if (DateTime.TryParse(s, CultureInfo.GetCultureInfo("ru-RU"), DateTimeStyles.AssumeLocal, out dt)) return dt.ToUniversalTime();
                }
            }
            return null;
        }
    }
}

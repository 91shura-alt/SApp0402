using Microsoft.EntityFrameworkCore;
using SellerOps.App.Data;
using SellerOps.App.Domain;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SellerOps.App.Services
{
    public class WbCatalogService
    {
        private readonly AppDbContext _db;
        private readonly WbHttpClientFactory _http = new();

        public WbCatalogService(AppDbContext? db = null)
        {
            _db = db ?? AppDbContext.Instance;
        }

        private (string token, string baseUrl) GetCreds()
        {
            var localToken = AppSettings.Instance.EncryptedContentToken;
            if (!string.IsNullOrWhiteSpace(localToken))
            {
                var localTokenValue = SecureStorage.Unprotect(localToken)?.Trim();
                if (string.IsNullOrWhiteSpace(localTokenValue))
                    throw new InvalidOperationException("Локальный токен 'Content' пустой/не расшифровался. Открой WB настройки и сохрани токен заново.");

                var localBaseUrl = AppSettings.Instance.ContentIsSandbox
                    ? "https://content-api-sandbox.wildberries.ru"
                    : "https://content-api.wildberries.ru";

                return (localTokenValue!, localBaseUrl);
            }

            var row = _db.ApiTokens.AsNoTracking()
                .OrderByDescending(x => x.Id)
                .FirstOrDefault(x => x.Category == "Content");

            if (row == null)
                throw new InvalidOperationException("Не найден токен категории 'Content'. Открой WB настройки и добавь токен.");

            if (!SecureStorage.TryUnprotect(row.EncryptedToken, out var token))
                throw new InvalidOperationException("Токен 'Content' был сохранён на другом ПК/пользователе. Пересохраните токен в настройках WB или включите режим хранения без шифрования.");

            token = token?.Trim();
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("Токен 'Content' пустой/не расшифровался.");

            var baseUrl = row.IsSandbox
                ? "https://content-api-sandbox.wildberries.ru"
                : "https://content-api.wildberries.ru";

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

        private static string BuildRequestJson(int limit, string? updatedAt, long nmIdCursor)
        {
            var sb = new StringBuilder();
            sb.Append("{\"settings\":{\"cursor\":{");
            sb.Append("\"limit\":").Append(limit);
            if (!string.IsNullOrEmpty(updatedAt))
                sb.Append(",\"updatedAt\":\"").Append(updatedAt).Append("\"");
            if (nmIdCursor > 0)
                sb.Append(",\"nmID\":").Append(nmIdCursor);
            sb.Append("},\"filter\":{\"withPhoto\":-1}}}");
            return sb.ToString();
        }

        // ---------------- Retry (429) ----------------

        private static async Task<HttpResponseMessage> PostJsonWithRetryAsync(
            HttpClient http,
            string url,
            string json,
            CancellationToken ct,
            int maxAttempts = 6)
        {
            // попытки: 1..maxAttempts
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                var resp = await http.PostAsync(url, content, ct);

                if (resp.StatusCode != (HttpStatusCode)429)
                    return resp;

                // 429: rate limit
                // читаем Retry-After если есть
                TimeSpan delay = TimeSpan.FromSeconds(2);

                if (resp.Headers.TryGetValues("Retry-After", out var vals))
                {
                    var v = vals.FirstOrDefault();
                    if (int.TryParse(v, out var seconds) && seconds > 0)
                        delay = TimeSpan.FromSeconds(Math.Min(60, seconds));
                }
                else
                {
                    // backoff: 2s, 4s, 8s, 16s, ...
                    var sec = Math.Min(60, (int)Math.Pow(2, attempt));
                    delay = TimeSpan.FromSeconds(sec);
                }

                resp.Dispose();

                // если это последняя попытка — выходим с нормальной ошибкой
                if (attempt == maxAttempts)
                    throw new InvalidOperationException("WB Content API вернул 429 слишком много раз. Подожди 30-60 сек и повтори.");

                await Task.Delay(delay, ct);
            }

            // недостижимо
            throw new InvalidOperationException("Не удалось выполнить запрос (retry exhausted).");
        }

        /// <summary>
        /// Синхронизация списка товаров в WbProducts (offline).
        /// Заполняем: Title/Brand/Subject/VendorCode/Barcode/BarcodesJson/RawJson + габариты/вес/объём.
        /// </summary>
        public async Task<int> SyncProductsAsync(CancellationToken ct = default)
        {
            var (token, baseUrl) = GetCreds();
            using var http = _http.Create(baseUrl, token, bearerHeader: false);

            int updated = 0;
            string? updatedAt = null;
            long cursorNm = 0;

            for (int page = 1; page <= 2000; page++)
            {
                ct.ThrowIfCancellationRequested();

                var reqBody = BuildRequestJson(limit: 100, updatedAt: updatedAt, nmIdCursor: cursorNm);

                using var resp = await PostJsonWithRetryAsync(http, "/content/v2/get/cards/list", reqBody, ct);
                var payload = await resp.Content.ReadAsStringAsync(ct);

                await SaveRawAsync(
                    "content_cards_list",
                    $"content_cards_list_p{page:000}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json",
                    payload,
                    ct);

                if (!resp.IsSuccessStatusCode)
                    throw new InvalidOperationException($"WB Content cards/list вернул {(int)resp.StatusCode}. См. WbRawFiles/logs.");

                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;
                var data = root.TryGetProperty("data", out var d) ? d : root;

                if (!data.TryGetProperty("cards", out var cards) || cards.ValueKind != JsonValueKind.Array)
                    break;

                foreach (var card in cards.EnumerateArray())
                {
                    var nmId = TryGetInt64(card, "nmID") ?? TryGetInt64(card, "nmId") ?? 0;
                    if (nmId <= 0) continue;

                    var title = TryGetString(card, "title") ?? "";
                    var brand = TryGetString(card, "brand") ?? "";
                    var subject = TryGetString(card, "subjectName") ?? TryGetString(card, "subject") ?? "";
                    var vendorCode = TryGetString(card, "vendorCode") ?? "";
                    var archived = TryGetBool(card, "archived") ?? false;

                    var barcodes = ExtractBarcodes(card);
                    var firstBarcode = barcodes.FirstOrDefault();

                    // габариты/вес
                    double? L = null, W = null, H = null, weightKg = null;

                    if (card.TryGetProperty("dimensions", out var dim) && dim.ValueKind == JsonValueKind.Object)
                        ReadDimensions(dim, ref L, ref W, ref H, ref weightKg);

                    if ((!L.HasValue || !W.HasValue || !H.HasValue || !weightKg.HasValue) &&
                        card.TryGetProperty("sizes", out var sizes) && sizes.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var s in sizes.EnumerateArray())
                        {
                            if (s.TryGetProperty("dimensions", out var d2) && d2.ValueKind == JsonValueKind.Object)
                                ReadDimensions(d2, ref L, ref W, ref H, ref weightKg);

                            if (!weightKg.HasValue && TryGetDouble(s, "weight") is double wSize)
                                weightKg = NormalizeWeightKg(wSize);

                            if (L.HasValue && W.HasValue && H.HasValue && weightKg.HasValue)
                                break;
                        }
                    }

                    double? volumeM3 = (L.HasValue && W.HasValue && H.HasValue)
                        ? (L.Value * W.Value * H.Value) / 1_000_000.0
                        : null;

                    var dbRow = await _db.WbProducts.FirstOrDefaultAsync(x => x.NmId == nmId, ct);
                    if (dbRow == null)
                    {
                        dbRow = new WbProduct { NmId = nmId };
                        _db.WbProducts.Add(dbRow);
                    }

                    dbRow.Title = title;
                    dbRow.Brand = brand;
                    dbRow.Subject = subject;
                    dbRow.VendorCode = vendorCode;

                    if (string.IsNullOrWhiteSpace(dbRow.Article))
                        dbRow.Article = vendorCode;

                    dbRow.Barcode = firstBarcode;
                    dbRow.BarcodesJson = barcodes.Count > 0 ? JsonSerializer.Serialize(barcodes) : null;

                    dbRow.IsArchived = archived;

                    dbRow.LengthCm = L;
                    dbRow.WidthCm = W;
                    dbRow.HeightCm = H;
                    dbRow.WeightKg = weightKg;
                    dbRow.VolumeM3 = volumeM3;

                    dbRow.RawJson = card.GetRawText();
                    dbRow.SyncedAt = DateTime.UtcNow;

                    updated++;
                }

                if (data.TryGetProperty("cursor", out var cursor))
                {
                    if (cursor.TryGetProperty("updatedAt", out var u) && u.ValueKind == JsonValueKind.String)
                        updatedAt = u.GetString();

                    if (cursor.TryGetProperty("nmID", out var nmid) && nmid.ValueKind == JsonValueKind.Number && nmid.TryGetInt64(out var v))
                        cursorNm = v;
                }

                await _db.SaveChangesAsync(ct);

                if (cards.GetArrayLength() < 100)
                    break;
            }

            return updated;
        }

        // ---------------- Details cache (10 минут) ----------------

        private static readonly ConcurrentDictionary<long, (WbCardDetails dto, DateTime ts)> _detailsCache
            = new ConcurrentDictionary<long, (WbCardDetails, DateTime)>();

        public class WbCardDetails
        {
            public long NmId { get; set; }
            public string? Title { get; set; }
            public string? Brand { get; set; }
            public string? Subject { get; set; }
            public string? VendorCode { get; set; }
            public string? Description { get; set; }
            public bool IsArchived { get; set; }
            public List<string>? Barcodes { get; set; }
            public List<CharDto>? Characteristics { get; set; }
            public double? LengthCm { get; set; }
            public double? WidthCm { get; set; }
            public double? HeightCm { get; set; }
            public double? WeightKg { get; set; }
            public string? RawJson { get; set; }

            public class CharDto
            {
                public string Name { get; set; } = "";
                public List<string>? Values { get; set; }
            }
        }

        public async Task<WbCardDetails> GetCardDetailsAsync(long nmId, CancellationToken ct = default)
        {
            if (_detailsCache.TryGetValue(nmId, out var cache) &&
                (DateTime.UtcNow - cache.ts) < TimeSpan.FromMinutes(10))
                return cache.dto;

            var (token, baseUrl) = GetCreds();
            using var http = _http.Create(baseUrl, token, bearerHeader: false);

            var filterBody =
                $"{{\"settings\":{{\"cursor\":{{\"limit\":1}},\"filter\":{{\"withPhoto\":-1,\"nmIDs\":[{nmId}]}}}}}}";

            using var resp = await PostJsonWithRetryAsync(http, "/content/v2/get/cards/list", filterBody, ct);
            var payload = await resp.Content.ReadAsStringAsync(ct);

            await SaveRawAsync(
                "content_card_details",
                $"content_card_details_{nmId}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json",
                payload,
                ct);

            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"WB Content card_details вернул {(int)resp.StatusCode}. См. WbRawFiles/logs.");

            var dto = TryParseSingleCard(payload, nmId);

            // ВАЖНО: если WB вернул пусто — не падаем, UI должен открыться
            if (dto == null)
            {
                dto = new WbCardDetails
                {
                    NmId = nmId,
                    Title = "",
                    Brand = "",
                    Subject = "",
                    VendorCode = "",
                    Description = "",
                    IsArchived = false,
                    Barcodes = new List<string>(),
                    Characteristics = new List<WbCardDetails.CharDto>(),
                    RawJson = payload
                };
            }

            _detailsCache[nmId] = (dto, DateTime.UtcNow);
            return dto;
        }

        private static WbCardDetails? TryParseSingleCard(string payload, long nmId)
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            var data = root.TryGetProperty("data", out var d) ? d : root;

            if (!data.TryGetProperty("cards", out var cards) || cards.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var c in cards.EnumerateArray())
            {
                var id = TryGetInt64(c, "nmID") ?? TryGetInt64(c, "nmId") ?? 0;
                if (id != nmId) continue;

                var dto = new WbCardDetails
                {
                    NmId = id,
                    Title = TryGetStringFromAny(c, "title", "name") ?? "",
                    Brand = TryGetStringFromAny(c, "brand", "brandName") ?? "",
                    Subject = TryGetStringFromAny(c, "subjectName", "subject", "subjectNameRu") ?? "",
                    VendorCode = TryGetStringFromAny(c, "vendorCode", "supplierArticle", "article") ?? "",
                    Description = TryGetStringFromAny(c, "description", "descriptionRu", "descriptionEn", "descriptionEN") ?? "",
                    IsArchived = TryGetBool(c, "archived") ?? false,
                    Barcodes = ExtractBarcodes(c),
                    Characteristics = ExtractCharacteristics(c),
                    RawJson = c.GetRawText()
                };

                // размеры/вес
                double? L = null, W = null, H = null, weightKg = null;

                if (c.TryGetProperty("dimensions", out var dim) && dim.ValueKind == JsonValueKind.Object)
                    ReadDimensions(dim, ref L, ref W, ref H, ref weightKg);

                if ((!L.HasValue || !W.HasValue || !H.HasValue || !weightKg.HasValue) &&
                    c.TryGetProperty("sizes", out var sizes) && sizes.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in sizes.EnumerateArray())
                    {
                        if (s.TryGetProperty("dimensions", out var d2) && d2.ValueKind == JsonValueKind.Object)
                            ReadDimensions(d2, ref L, ref W, ref H, ref weightKg);

                        if (!weightKg.HasValue && TryGetDouble(s, "weight") is double wSize)
                            weightKg = NormalizeWeightKg(wSize);

                        if (L.HasValue && W.HasValue && H.HasValue && weightKg.HasValue)
                            break;
                    }
                }

                dto.LengthCm = L;
                dto.WidthCm = W;
                dto.HeightCm = H;
                dto.WeightKg = weightKg;

                return dto;
            }

            return null;
        }

        // ---------------- helpers ----------------

        private static List<string> ExtractBarcodes(JsonElement card)
        {
            var bcs = new List<string>();

            if (card.TryGetProperty("sizes", out var sizes) && sizes.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in sizes.EnumerateArray())
                {
                    if (s.TryGetProperty("skus", out var skus) && skus.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var b in skus.EnumerateArray())
                        {
                            var val = b.GetString();
                            if (!string.IsNullOrWhiteSpace(val))
                                bcs.Add(val);
                        }
                    }
                }
            }

            return bcs.Distinct().ToList();
        }

        private static List<WbCardDetails.CharDto> ExtractCharacteristics(JsonElement c)
        {
            var list = new List<WbCardDetails.CharDto>();

            if (c.TryGetProperty("characteristics", out var chs) && chs.ValueKind == JsonValueKind.Array)
            {
                foreach (var ch in chs.EnumerateArray())
                {
                    var name = TryGetString(ch, "name") ?? "";
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    var values = new List<string>();

                    if (ch.TryGetProperty("value", out var v))
                    {
                        if (v.ValueKind == JsonValueKind.Array)
                            values.AddRange(v.EnumerateArray().Select(x => x.ToString()));
                        else if (v.ValueKind != JsonValueKind.Null)
                            values.Add(v.ToString());
                    }

                    list.Add(new WbCardDetails.CharDto { Name = name, Values = values });
                }
            }
            else if (c.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Array)
            {
                foreach (var ch in attrs.EnumerateArray())
                {
                    var name = TryGetString(ch, "name") ?? "";
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    var values = new List<string>();
                    if (ch.TryGetProperty("value", out var v))
                    {
                        if (v.ValueKind == JsonValueKind.Array)
                            values.AddRange(v.EnumerateArray().Select(x => x.ToString()));
                        else if (v.ValueKind != JsonValueKind.Null)
                            values.Add(v.ToString());
                    }

                    list.Add(new WbCardDetails.CharDto { Name = name, Values = values });
                }
            }

            return list;
        }

        private static void ReadDimensions(JsonElement dim, ref double? L, ref double? W, ref double? H, ref double? weightKg)
        {
            if (dim.ValueKind != JsonValueKind.Object) return;

            L ??= TryGetDouble(dim, "length") ?? TryGetDouble(dim, "L");
            W ??= TryGetDouble(dim, "width") ?? TryGetDouble(dim, "W");
            H ??= TryGetDouble(dim, "height") ?? TryGetDouble(dim, "H");

            if (!weightKg.HasValue)
            {
                if (TryGetDouble(dim, "weight") is double w) weightKg = NormalizeWeightKg(w);
                else if (TryGetDouble(dim, "weightGr") is double wg) weightKg = wg / 1000.0;
                else if (TryGetDouble(dim, "weightKg") is double wk) weightKg = wk;
            }
        }

        private static string? TryGetString(JsonElement e, string name)
        {
            return e.TryGetProperty(name, out var p)
                ? (p.ValueKind == JsonValueKind.String ? p.GetString() : p.ToString())
                : null;
        }

        private static string? TryGetStringFromAny(JsonElement e, params string[] names)
        {
            foreach (var name in names)
            {
                var value = TryGetString(e, name);
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            return null;
        }

        private static long? TryGetInt64(JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out var p)) return null;
            if (p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var v)) return v;
            if (p.ValueKind == JsonValueKind.String && long.TryParse(p.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var v2)) return v2;
            return null;
        }

        private static bool? TryGetBool(JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out var p)) return null;
            if (p.ValueKind == JsonValueKind.True) return true;
            if (p.ValueKind == JsonValueKind.False) return false;
            if (p.ValueKind == JsonValueKind.String && bool.TryParse(p.GetString(), out var b)) return b;
            return null;
        }

        private static double? TryGetDouble(JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out var p)) return null;
            if (p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var d)) return d;
            if (p.ValueKind == JsonValueKind.String &&
                double.TryParse(p.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var ds)) return ds;
            return null;
        }

        private static double NormalizeWeightKg(double raw) => raw > 10 ? raw / 1000.0 : raw;
    }
}

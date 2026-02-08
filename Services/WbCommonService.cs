using Microsoft.EntityFrameworkCore;
using SellerOps.App.Data;
using SellerOps.App.Domain;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SellerOps.App.Services
{
    /// <summary>
    /// WB Common API.
    /// Сейчас используем:
    /// - seller-info (инфо о продавце)
    /// - новости портала продавцов
    ///
    /// Важно: домен у Common API отдельный: common-api.wildberries.ru.
    /// </summary>
    public sealed class WbCommonService
    {
        private readonly AppDbContext _db;
        private readonly WbHttpClientFactory _http = new();

        public WbCommonService(AppDbContext? db = null)
        {
            _db = db ?? AppDbContext.Instance;
        }

        private (string token, string baseUrl) GetCreds()
        {
            var localToken = AppSettings.Instance.EncryptedCommonToken;
            if (!string.IsNullOrWhiteSpace(localToken))
            {
                var localTokenValue = SecureStorage.Unprotect(localToken)?.Trim();
                if (string.IsNullOrWhiteSpace(localTokenValue))
                    throw new InvalidOperationException("Локальный токен 'Common' пустой/не расшифровался. Открой WB настройки и сохрани токен заново.");

                var localBaseUrl = AppSettings.Instance.CommonIsSandbox
                    ? "https://common-api-sandbox.wildberries.ru"
                    : "https://common-api.wildberries.ru";

                return (localTokenValue!, localBaseUrl);
            }

            // 1) Пытаемся найти именно Common
            var row = _db.ApiTokens.AsNoTracking()
                .OrderByDescending(x => x.Id)
                .FirstOrDefault(x => x.Category == "Common");

            // 2) Если нет — берём любой токен (seller-info часто доступен любым токеном)
            row ??= _db.ApiTokens.AsNoTracking()
                .OrderByDescending(x => x.Id)
                .FirstOrDefault();

            if (row == null)
                throw new InvalidOperationException("Не найден ни один токен WB. Открой WB настройки и добавь токен.");

            if (!SecureStorage.TryUnprotect(row.EncryptedToken, out var token))
                throw new InvalidOperationException("Токен WB был сохранён на другом ПК/пользователе. Пересохраните токен в настройках WB или включите режим хранения без шифрования.");

            token = token?.Trim();
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("Токен пустой/не расшифровался. Открой WB настройки и добавь токен заново.");

            // Common API домен
            var baseUrl = row.IsSandbox
                ? "https://common-api-sandbox.wildberries.ru"
                : "https://common-api.wildberries.ru";

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

        // ---------------- seller-info ----------------

        public async Task<(string summary, string rawJson)> FetchSellerInfoAsync(CancellationToken ct = default)
        {
            var (token, baseUrl) = GetCreds();
            using var http = _http.Create(baseUrl, token, bearerHeader: false);

            using var resp = await http.GetAsync("/api/v1/seller-info", ct);
            var payload = await resp.Content.ReadAsStringAsync(ct);

            await SaveRawAsync(
                "common_seller_info",
                $"common_seller_info_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json",
                payload,
                ct);

            resp.EnsureSuccessStatusCode();

            // Формируем короткую сводку (если структура меняется — всё равно покажем сырой JSON)
            try
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;

                string name = TryGetString(root, "name") ?? TryGetString(root, "sellerName") ?? "";
                string inn = TryGetString(root, "inn") ?? "";
                string ogrn = TryGetString(root, "ogrn") ?? "";
                string id = (TryGetInt64(root, "id") ?? TryGetInt64(root, "sellerId"))?.ToString() ?? "";

                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(name)) parts.Add($"name: {name}");
                if (!string.IsNullOrWhiteSpace(id)) parts.Add($"id: {id}");
                if (!string.IsNullOrWhiteSpace(inn)) parts.Add($"inn: {inn}");
                if (!string.IsNullOrWhiteSpace(ogrn)) parts.Add($"ogrn: {ogrn}");

                var summary = parts.Count > 0 ? string.Join("; ", parts) : "OK";
                return (summary, payload);
            }
            catch
            {
                return ("OK", payload);
            }
        }

        // ---------------- news ----------------

        public sealed class NewsRow
        {
            // Новая основная дата (UTC) — чтобы сортировать и форматировать
            public DateTime? DtUtc { get; set; }

            // Совместимость со старым кодом: где-то мог использоваться Dt
            public DateTime? Dt
            {
                get => DtUtc;
                set => DtUtc = value;
            }

            public string? Title { get; set; }
            public string? Body { get; set; }
        }

        
        public async Task<(List<NewsRow> items, string rawJson)> FetchNewsAsync(
            DateTime? fromDate = null,
            long? fromId = null,
            CancellationToken ct = default)
        {
            var (token, baseUrl) = GetCreds();
            using var http = _http.Create(baseUrl, token, bearerHeader: false);

            // WB периодически менял формат параметров у news.
            // Поэтому используем "умный" перебор вариантов, чтобы не упираться в 400/404.
            var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(string q)
            {
                if (q == null) q = "";
                if (tried.Add(q))
                    _ = q; // just to keep intent
            }

            // Порядок важен: сначала самый простой.
            var candidates = new List<string>();
            void AddCandidate(string q)
            {
                q ??= "";
                if (tried.Add(q)) candidates.Add(q);
            }

            AddCandidate(""); // без параметров

            if (fromId.HasValue)
            {
                var id = fromId.Value;
                AddCandidate($"?fromId={id}");
                AddCandidate($"?fromID={id}");
                AddCandidate($"?fromid={id}");
            }

            // WB обычно принимает дату для параметров from/fromDate (без времени).
            // При этом встречаются разные имена параметров. Добавляем несколько вариантов.
            var dt = (fromDate ?? DateTime.UtcNow.AddDays(-30)).ToUniversalTime();
            var dateOnly = dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var iso = dt.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            var isoMidnight = dt.Date.ToString("yyyy-MM-dd'T'00:00:00'Z'", CultureInfo.InvariantCulture);

            // Самые вероятные варианты — без времени.
            AddCandidate($"?from={Uri.EscapeDataString(dateOnly)}");
            AddCandidate($"?fromDate={Uri.EscapeDataString(dateOnly)}");

            // Фоллбеки со временем (если у WB на конкретной версии API такое требуется).
            AddCandidate($"?from={Uri.EscapeDataString(isoMidnight)}");
            AddCandidate($"?fromDate={Uri.EscapeDataString(isoMidnight)}");
            AddCandidate($"?from={Uri.EscapeDataString(iso)}");
            AddCandidate($"?fromDate={Uri.EscapeDataString(iso)}");

            int lastStatus = 0;
            string lastReason = "";
            string lastUrl = "";

            foreach (var q in candidates)
            {
                var url = $"/api/communications/v2/news{q}";
                using var resp = await http.GetAsync(url, ct);
                var raw = await resp.Content.ReadAsStringAsync(ct);

                lastStatus = (int)resp.StatusCode;
                lastReason = resp.ReasonPhrase ?? "";
                lastUrl = $"{baseUrl}{url}";

                if (resp.IsSuccessStatusCode)
                {
                    await SaveRawAsync(
                        "common_news",
                        $"news_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json",
                        raw,
                        ct);
                    return (ParseNews(raw), raw);
                }

                // Логируем каждый неуспешный вариант, чтобы можно было быстро понять, чего не хватает.
                await SaveRawAsync(
                    "common_news_error",
                    $"news_error_{DateTime.UtcNow:yyyyMMdd_HHmmss}.txt",
                    $"URL: {lastUrl}\nSTATUS: {lastStatus} {lastReason}\nBODY:\n{raw}",
                    ct);

                // Эти статусы обычно означают "не угадали параметр" — пробуем следующий вариант.
                if (lastStatus == 400 || lastStatus == 404)
                    continue;

                throw new HttpRequestException(
                    $"WB Common news error {lastStatus} {lastReason}. См. WbRawFiles/logs. URL: {lastUrl}");
            }

            throw new HttpRequestException(
                $"WB Common news error {lastStatus} {lastReason}. См. WbRawFiles/logs. URL: {lastUrl}");
        }

private static List<NewsRow> ParseNews(string payload)
        {
            var list = new List<NewsRow>();

            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            // В зависимости от версии API может быть: { data:[...] } или просто [...]
            JsonElement arr;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var d))
                arr = d;
            else
                arr = root;

            if (arr.ValueKind != JsonValueKind.Array)
                return list;

            foreach (var it in arr.EnumerateArray())
            {
                var row = new NewsRow
                {
                    DtUtc = TryGetAnyDateUtc(it, "dt")
                        ?? TryGetAnyDateUtc(it, "date")
                        ?? TryGetAnyDateUtc(it, "createdAt")
                        ?? TryGetAnyDateUtc(it, "created")
                        ?? TryGetAnyDateUtc(it, "publishedAt"),

                    Title = TryGetString(it, "title") ?? TryGetString(it, "header") ?? "",
                    Body = TryGetString(it, "body") ?? TryGetString(it, "text") ?? ""
                };

                // Иногда тело новости может быть в поле content
                if (string.IsNullOrWhiteSpace(row.Body))
                    row.Body = TryGetString(it, "content") ?? "";

                list.Add(row);
            }

            return list;
        }

        private static DateTime? TryGetAnyDateUtc(JsonElement e, string prop)
        {
            if (!e.TryGetProperty(prop, out var p))
                return null;

            // timestamp (sec/ms)
            if (p.ValueKind == JsonValueKind.Number)
            {
                if (p.TryGetInt64(out var n))
                {
                    // эвристика: если очень большое — это ms
                    if (n > 1_000_000_000_000)
                        return DateTimeOffset.FromUnixTimeMilliseconds(n).UtcDateTime;

                    if (n > 1_000_000_000)
                        return DateTimeOffset.FromUnixTimeSeconds(n).UtcDateTime;
                }
                return null;
            }

            if (p.ValueKind != JsonValueKind.String)
                return null;

            var s = p.GetString();
            if (string.IsNullOrWhiteSpace(s))
                return null;

            // DateTimeOffset
            if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
                return dto.UtcDateTime;

            // DateTime without zone => считаем UTC
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
                return DateTime.SpecifyKind(dt, DateTimeKind.Utc);

            return null;
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
    }
}

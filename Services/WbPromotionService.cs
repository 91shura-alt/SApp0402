using Microsoft.EntityFrameworkCore;
using SellerOps.App.Data;
using SellerOps.App.Domain;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SellerOps.App.Services
{
    /// <summary>
    /// WB Promotion API (advert-api) — акции/продвижение.
    /// </summary>
    public class WbPromotionService
    {
        private readonly AppDbContext _db;
        private readonly WbHttpClientFactory _http = new();

        public WbPromotionService(AppDbContext? db = null)
        {
            _db = db ?? AppDbContext.Instance;
        }

        private (string token, string baseUrl) GetCreds()
        {
            var localToken = AppSettings.Instance.EncryptedPromotionToken;
            if (!string.IsNullOrWhiteSpace(localToken))
            {
                var localTokenValue = SecureStorage.Unprotect(localToken)?.Trim();
                if (string.IsNullOrWhiteSpace(localTokenValue))
                    throw new InvalidOperationException("Локальный токен 'Promotion' пустой/не расшифровался. Открой WB настройки и сохрани токен заново.");

                var localBaseUrl = AppSettings.Instance.PromotionIsSandbox
                    ? "https://advert-api-sandbox.wildberries.ru"
                    : "https://advert-api.wildberries.ru";

                return (localTokenValue!, localBaseUrl);
            }

            var row = _db.ApiTokens.AsNoTracking()
                .OrderByDescending(x => x.Id)
                .FirstOrDefault(x => x.Category == "Promotion");

            if (row == null)
                throw new InvalidOperationException("Не найден токен категории 'Promotion'. Открой WB настройки и добавь токен.");

            if (!SecureStorage.TryUnprotect(row.EncryptedToken, out var token))
                throw new InvalidOperationException("Токен 'Promotion' был сохранён на другом ПК/пользователе. Пересохраните токен в настройках WB или включите режим хранения без шифрования.");

            token = token?.Trim();
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("Токен 'Promotion' пустой/не расшифровался.");

            var baseUrl = row.IsSandbox
                ? "https://advert-api-sandbox.wildberries.ru"
                : "https://advert-api.wildberries.ru";

            return (token!, baseUrl);
        }

        private string GetCalendarBaseUrl()
        {
            return "https://promotion-api.wildberries.ru";
        }

        public async Task<int> RefreshPromotionsForNmIdAsync(long nmId, CancellationToken ct = default)
        {
            var (token, baseUrl) = GetCreds();
            var advertIds = await GetPromotionAdvertIdsAsync(baseUrl, token, ct);
            if (advertIds.Count == 0)
                throw new InvalidOperationException("WB Promotion: не удалось получить список кампаний (advertIds).");

            var payload = await GetPromotionsRawAsync(baseUrl, token, advertIds, ct);

            var parsed = ParsePromotions(payload, nmId);

            var importedAt = DateTime.UtcNow;
            var existing = await _db.WbPromotionItems.Where(x => x.NmId == nmId).ToListAsync(ct);
            if (existing.Count > 0)
                _db.WbPromotionItems.RemoveRange(existing);

            foreach (var item in parsed)
            {
                item.NmId = nmId;
                item.ImportedAtUtc = importedAt;
                _db.WbPromotionItems.Add(item);
            }

            await _db.SaveChangesAsync(ct);
            return parsed.Count;
        }

        public async Task<int> RefreshCalendarPromotionsForNmIdAsync(long nmId, CancellationToken ct = default)
        {
            var (token, _) = GetCreds();
            var baseUrl = GetCalendarBaseUrl();
            var payload = await GetCalendarPromotionsRawAsync(baseUrl, token, ct);
            var parsed = ParseCalendarPromotions(payload, nmId);

            var importedAt = DateTime.UtcNow;
            var existing = await _db.WbPromotionCalendarItems.Where(x => x.NmId == nmId).ToListAsync(ct);
            if (existing.Count > 0)
                _db.WbPromotionCalendarItems.RemoveRange(existing);

            foreach (var item in parsed)
            {
                item.NmId = nmId;
                item.ImportedAtUtc = importedAt;
                _db.WbPromotionCalendarItems.Add(item);
            }

            await _db.SaveChangesAsync(ct);
            return parsed.Count;
        }

        private async Task<List<long>> GetPromotionAdvertIdsAsync(string baseUrl, string token, CancellationToken ct)
        {
            using var http = _http.Create(baseUrl, token, bearerHeader: false);
            using var resp = await http.GetAsync("/adv/v1/promotion/count", ct);
            var payload = await resp.Content.ReadAsStringAsync(ct);

            if (resp.IsSuccessStatusCode)
                return ExtractAdvertIds(payload);

            if ((int)resp.StatusCode == 401)
            {
                using var httpBearer = _http.Create(baseUrl, token, bearerHeader: true);
                using var respBearer = await httpBearer.GetAsync("/adv/v1/promotion/count", ct);
                var payloadBearer = await respBearer.Content.ReadAsStringAsync(ct);

                if (respBearer.IsSuccessStatusCode)
                    return ExtractAdvertIds(payloadBearer);

                throw new InvalidOperationException($"WB Promotion count вернул {(int)respBearer.StatusCode}. {payloadBearer}");
            }

            throw new InvalidOperationException($"WB Promotion count вернул {(int)resp.StatusCode}. {payload}");
        }

        private async Task<string> GetCalendarPromotionsRawAsync(string baseUrl, string token, CancellationToken ct)
        {
            const int maxAttempts = 6;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                using var http = _http.Create(baseUrl, token, bearerHeader: false);
                using var resp = await http.GetAsync("/api/v1/calendar/promotions", ct);
                var payload = await resp.Content.ReadAsStringAsync(ct);

                if (resp.IsSuccessStatusCode)
                    return payload;

                if ((int)resp.StatusCode == 401)
                {
                    using var httpBearer = _http.Create(baseUrl, token, bearerHeader: true);
                    using var respBearer = await httpBearer.GetAsync("/api/v1/calendar/promotions", ct);
                    var payloadBearer = await respBearer.Content.ReadAsStringAsync(ct);

                    if (respBearer.IsSuccessStatusCode)
                        return payloadBearer;

                    if (respBearer.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                    {
                        if (attempt == maxAttempts)
                            throw new InvalidOperationException("WB Promotion calendar вернул 429 слишком много раз. Подожди 30-60 сек и повтори.");

                        await Task.Delay(GetRetryDelay(respBearer, attempt), ct);
                        continue;
                    }

                    throw new InvalidOperationException($"WB Promotion calendar вернул {(int)respBearer.StatusCode}. {payloadBearer}");
                }

                if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    if (attempt == maxAttempts)
                        throw new InvalidOperationException("WB Promotion calendar вернул 429 слишком много раз. Подожди 30-60 сек и повтори.");

                    await Task.Delay(GetRetryDelay(resp, attempt), ct);
                    continue;
                }

                throw new InvalidOperationException($"WB Promotion calendar вернул {(int)resp.StatusCode}. {payload}");
            }

            throw new InvalidOperationException("WB Promotion calendar: не удалось выполнить запрос (retry exhausted).");
        }

        private async Task<string> GetPromotionsRawAsync(string baseUrl, string token, IReadOnlyCollection<long> advertIds, CancellationToken ct)
        {
            var chunkList = advertIds.Distinct().Chunk(50).ToList();
            var responses = new List<string>();
            const int maxAttempts = 10;
            var delayBetweenChunks = TimeSpan.FromMilliseconds(300);

            for (var i = 0; i < chunkList.Count; i++)
            {
                var chunk = chunkList[i];
                var json = JsonSerializer.Serialize(chunk);
                var handled = false;

                for (int attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    ct.ThrowIfCancellationRequested();

                    using var http = _http.Create(baseUrl, token, bearerHeader: false);
                    using var resp = await http.PostAsync("/adv/v1/promotion/adverts", new StringContent(json, System.Text.Encoding.UTF8, "application/json"), ct);
                    var payload = await resp.Content.ReadAsStringAsync(ct);

                    if (resp.IsSuccessStatusCode)
                    {
                        responses.Add(payload);
                        handled = true;
                        break;
                    }

                    if ((int)resp.StatusCode == 401)
                    {
                        using var httpBearer = _http.Create(baseUrl, token, bearerHeader: true);
                        using var respBearer = await httpBearer.PostAsync("/adv/v1/promotion/adverts", new StringContent(json, System.Text.Encoding.UTF8, "application/json"), ct);
                        var payloadBearer = await respBearer.Content.ReadAsStringAsync(ct);

                        if (respBearer.IsSuccessStatusCode)
                        {
                            responses.Add(payloadBearer);
                            handled = true;
                            break;
                        }

                        if (respBearer.StatusCode == (System.Net.HttpStatusCode)429)
                        {
                            if (attempt == maxAttempts)
                                throw new InvalidOperationException("WB Promotion adverts вернул 429 слишком много раз. Подожди 30-60 сек и повтори.");

                            await Task.Delay(GetRetryDelay(respBearer, attempt), ct);
                            continue;
                        }

                        throw new InvalidOperationException($"WB Promotion adverts вернул {(int)respBearer.StatusCode}. {payloadBearer}");
                    }

                    if (resp.StatusCode == (System.Net.HttpStatusCode)429)
                    {
                        if (attempt == maxAttempts)
                            throw new InvalidOperationException("WB Promotion adverts вернул 429 слишком много раз. Подожди 30-60 сек и повтори.");

                        await Task.Delay(GetRetryDelay(resp, attempt), ct);
                        continue;
                    }

                    throw new InvalidOperationException($"WB Promotion adverts вернул {(int)resp.StatusCode}. {payload}");
                }

                if (!handled)
                    throw new InvalidOperationException("WB Promotion adverts: не удалось выполнить запрос (retry exhausted).");

                if (i < chunkList.Count - 1)
                    await Task.Delay(delayBetweenChunks, ct);
            }

            return $"[{string.Join(',', responses)}]";
        }

        private static TimeSpan GetRetryDelay(HttpResponseMessage resp, int attempt)
        {
            if (resp.Headers.TryGetValues("Retry-After", out var vals))
            {
                var v = vals.FirstOrDefault();
                if (int.TryParse(v, out var seconds) && seconds > 0)
                    return TimeSpan.FromSeconds(Math.Min(60, seconds));
            }

            var sec = Math.Min(60, (int)Math.Pow(2, attempt));
            return TimeSpan.FromSeconds(sec);
        }

        private static List<long> ExtractAdvertIds(string payload)
        {
            var result = new List<long>();
            try
            {
                using var doc = JsonDocument.Parse(payload);
                ExtractAdvertIds(doc.RootElement, result);
            }
            catch
            {
                return result;
            }

            return result.Distinct().ToList();
        }

        private static void ExtractAdvertIds(JsonElement element, List<long> result)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var prop in element.EnumerateObject())
                    {
                        if (prop.NameEquals("advertId") || prop.NameEquals("advert_id") || prop.NameEquals("id"))
                        {
                            if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt64(out var v))
                                result.Add(v);
                            else if (prop.Value.ValueKind == JsonValueKind.String && long.TryParse(prop.Value.GetString(), out var vs))
                                result.Add(vs);
                        }

                        ExtractAdvertIds(prop.Value, result);
                    }
                    break;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                        ExtractAdvertIds(item, result);
                    break;
            }
        }

        private static List<WbPromotionItem> ParsePromotions(string payload, long nmId)
        {
            var result = new List<WbPromotionItem>();

            try
            {
                using var doc = JsonDocument.Parse(payload);
                ExtractPromotions(doc.RootElement, nmId, result);
            }
            catch
            {
                // игнор — вернём пустой список
            }

            return result;
        }

        private static List<WbPromotionCalendarItem> ParseCalendarPromotions(string payload, long nmId)
        {
            var result = new List<WbPromotionCalendarItem>();

            try
            {
                using var doc = JsonDocument.Parse(payload);
                ExtractCalendarPromotions(doc.RootElement, nmId, result);
            }
            catch
            {
                // игнор — вернём пустой список
            }

            return result;
        }

        private static void ExtractPromotions(JsonElement element, long nmId, List<WbPromotionItem> result)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    if (ContainsNm(element, nmId))
                    {
                        var name = TryGetStringFromAny(element, "name", "title");
                        var required = TryGetStringFromAny(element, "requiredDiscount", "minDiscount", "discount", "discountPercent");
                        var status = TryGetStringFromAny(element, "status", "state");
                        var details = TryGetStringFromAny(element, "comment", "details", "description");
                        var promoId = TryGetInt64FromAny(element, "id", "promotionId", "advertId");

                        if (string.IsNullOrWhiteSpace(name)
                            && string.IsNullOrWhiteSpace(required)
                            && string.IsNullOrWhiteSpace(status)
                            && string.IsNullOrWhiteSpace(details)
                            && !promoId.HasValue)
                        {
                            break;
                        }

                        result.Add(new WbPromotionItem
                        {
                            PromotionId = promoId,
                            Name = string.IsNullOrWhiteSpace(name) ? "Акция" : name,
                            RequiredDiscount = required ?? "",
                            Status = status ?? "",
                            Details = details ?? "",
                            RawJson = element.GetRawText()
                        });
                    }

                    foreach (var prop in element.EnumerateObject())
                        ExtractPromotions(prop.Value, nmId, result);
                    break;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                        ExtractPromotions(item, nmId, result);
                    break;
            }
        }

        private static void ExtractCalendarPromotions(JsonElement element, long nmId, List<WbPromotionCalendarItem> result)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    if (ContainsNmCalendar(element, nmId))
                    {
                        var name = TryGetStringFromAny(element, "action_name", "name", "title");
                        var status = TryGetStringFromAny(element, "status", "state");
                        var participation = TryGetStringFromAny(element, "participation", "participate", "isParticipating", "participationStatus");
                        var dateFrom = TryGetStringFromAny(element, "date_from", "dateFrom", "from", "start", "startDate");
                        var dateTo = TryGetStringFromAny(element, "date_to", "dateTo", "to", "end", "endDate");
                        var details = TryGetStringFromAny(element, "message", "comment", "details", "description", "bottomText1", "bottomText2");
                        var promoId = TryGetInt64FromAny(element, "id", "promotionId", "advertId", "actionId");

                        if (string.IsNullOrWhiteSpace(name)
                            && string.IsNullOrWhiteSpace(status)
                            && string.IsNullOrWhiteSpace(participation)
                            && string.IsNullOrWhiteSpace(dateFrom)
                            && string.IsNullOrWhiteSpace(dateTo)
                            && string.IsNullOrWhiteSpace(details)
                            && !promoId.HasValue)
                        {
                            break;
                        }

                        result.Add(new WbPromotionCalendarItem
                        {
                            PromotionId = promoId,
                            Name = string.IsNullOrWhiteSpace(name) ? "Акция" : name,
                            Status = status ?? "",
                            Participation = participation ?? "",
                            DateFrom = dateFrom ?? "",
                            DateTo = dateTo ?? "",
                            Details = details ?? "",
                            RawJson = element.GetRawText()
                        });
                    }

                    foreach (var prop in element.EnumerateObject())
                        ExtractCalendarPromotions(prop.Value, nmId, result);
                    break;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                        ExtractCalendarPromotions(item, nmId, result);
                    break;
            }
        }

        private static bool ContainsNm(JsonElement element, long nmId)
        {
            if (TryGetInt64FromAny(element, "nmId", "nm", "nmID") == nmId)
                return true;

            if (ContainsNmInArray(element, nmId, "nms", "nmIds", "nm_ids", "nmID"))
                return true;

            return false;
        }

        private static bool ContainsNmCalendar(JsonElement element, long nmId)
        {
            if (TryGetInt64FromAny(element, "nmId", "nm", "nmID") == nmId)
                return true;

            if (ContainsNmInArray(element, nmId, "nms", "nmIds", "nm_ids", "nomenclatures", "nomenclatureIds"))
                return true;

            return false;
        }

        private static bool ContainsNmInArray(JsonElement element, long nmId, params string[] names)
        {
            foreach (var name in names)
            {
                if (!element.TryGetProperty(name, out var nms) || nms.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var item in nms.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var v) && v == nmId)
                        return true;
                    if (item.ValueKind == JsonValueKind.String && long.TryParse(item.GetString(), out var vs) && vs == nmId)
                        return true;
                    if (item.ValueKind == JsonValueKind.Object
                        && TryGetInt64FromAny(item, "nmId", "nm", "nmID") == nmId)
                        return true;
                }
            }

            return false;
        }

        private static string? TryGetStringFromAny(JsonElement element, params string[] names)
        {
            foreach (var name in names)
            {
                if (element.TryGetProperty(name, out var p))
                {
                    if (p.ValueKind == JsonValueKind.String)
                        return p.GetString();
                    if (p.ValueKind != JsonValueKind.Null)
                        return p.ToString();
                }
            }

            return null;
        }

        private static long? TryGetInt64FromAny(JsonElement element, params string[] names)
        {
            foreach (var name in names)
            {
                if (element.TryGetProperty(name, out var p))
                {
                    if (p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var v))
                        return v;
                    if (p.ValueKind == JsonValueKind.String && long.TryParse(p.GetString(), out var s))
                        return s;
                }
            }

            return null;
        }
    }
}

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

        private (string token, bool isSandbox) GetPricesAndDiscountsCreds()
        {
            var localToken = AppSettings.Instance.EncryptedPricesAndDiscountsToken;
            if (!string.IsNullOrWhiteSpace(localToken))
            {
                var localTokenValue = SecureStorage.Unprotect(localToken)?.Trim();
                if (string.IsNullOrWhiteSpace(localTokenValue))
                    throw new InvalidOperationException("Локальный токен 'PricesAndDiscounts' пустой/не расшифровался. Открой WB настройки и сохрани токен заново.");

                return (localTokenValue!, AppSettings.Instance.PricesAndDiscountsIsSandbox);
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

            return (token!, row.IsSandbox);
        }

        private static string GetCalendarBaseUrl(bool isSandbox)
        {
            return isSandbox
                ? "https://discounts-prices-api-sandbox.wildberries.ru"
                : "https://dp-calendar-api.wildberries.ru";
        }

        public async Task<int> RefreshPromotionsForNmIdAsync(long nmId, CancellationToken ct = default)
        {
            var (token, isSandbox) = GetPricesAndDiscountsCreds();
            var baseUrl = GetCalendarBaseUrl(isSandbox);
            var parsed = await LoadPromotionsFromCalendarAsync(baseUrl, token, nmId, ct);

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
            var (token, isSandbox) = GetPricesAndDiscountsCreds();
            var baseUrl = GetCalendarBaseUrl(isSandbox);
            var parsed = await LoadCalendarEntriesAsync(baseUrl, token, nmId, ct);

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
            var url = BuildCalendarUrl("/api/v1/calendar/promotions", null);

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                using var http = _http.Create(baseUrl, token, bearerHeader: false);
                using var resp = await http.GetAsync(url, ct);
                var payload = await resp.Content.ReadAsStringAsync(ct);

                if (resp.IsSuccessStatusCode)
                {
                    await SaveRawAsync("promo_calendar_promotions", $"promo_calendar_promotions_{DateTime.UtcNow:yyyyMMddHHmmss}.json", payload, ct);
                    return payload;
                }

                if ((int)resp.StatusCode == 401)
                {
                    using var httpBearer = _http.Create(baseUrl, token, bearerHeader: true);
                    using var respBearer = await httpBearer.GetAsync(url, ct);
                    var payloadBearer = await respBearer.Content.ReadAsStringAsync(ct);

                    if (respBearer.IsSuccessStatusCode)
                    {
                        await SaveRawAsync("promo_calendar_promotions", $"promo_calendar_promotions_{DateTime.UtcNow:yyyyMMddHHmmss}.json", payloadBearer, ct);
                        return payloadBearer;
                    }

                    if (respBearer.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                    {
                        if (attempt == maxAttempts)
                            throw new InvalidOperationException("WB Promotion calendar вернул 429 слишком много раз. Подожди 30-60 сек и повтори.");

                        await Task.Delay(GetRetryDelay(respBearer, attempt), ct);
                        continue;
                    }

                    throw new InvalidOperationException(BuildHttpErrorMessage("WB Promotion calendar", url, respBearer, payloadBearer));
                }

                if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    if (attempt == maxAttempts)
                        throw new InvalidOperationException("WB Promotion calendar вернул 429 слишком много раз. Подожди 30-60 сек и повтори.");

                    await Task.Delay(GetRetryDelay(resp, attempt), ct);
                    continue;
                }

                throw new InvalidOperationException(BuildHttpErrorMessage("WB Promotion calendar", url, resp, payload));
            }

            throw new InvalidOperationException("WB Promotion calendar: не удалось выполнить запрос (retry exhausted).");
        }

        private async Task<string> GetCalendarNomenclaturesRawAsync(string baseUrl, string token, long promotionId, bool inAction, CancellationToken ct)
        {
            const int maxAttempts = 6;
            var url = BuildCalendarNomenclaturesUrl(promotionId, inAction);

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                using var http = _http.Create(baseUrl, token, bearerHeader: false);
                using var resp = await http.GetAsync(url, ct);
                var payload = await resp.Content.ReadAsStringAsync(ct);

                if (resp.IsSuccessStatusCode)
                {
                    await SaveRawAsync("promo_calendar_nomenclatures", $"promo_calendar_nomenclatures_{promotionId}_{inAction}_{DateTime.UtcNow:yyyyMMddHHmmss}.json", payload, ct);
                    return payload;
                }

                if ((int)resp.StatusCode == 401)
                {
                    using var httpBearer = _http.Create(baseUrl, token, bearerHeader: true);
                    using var respBearer = await httpBearer.GetAsync(url, ct);
                    var payloadBearer = await respBearer.Content.ReadAsStringAsync(ct);

                    if (respBearer.IsSuccessStatusCode)
                    {
                        await SaveRawAsync("promo_calendar_nomenclatures", $"promo_calendar_nomenclatures_{promotionId}_{inAction}_{DateTime.UtcNow:yyyyMMddHHmmss}.json", payloadBearer, ct);
                        return payloadBearer;
                    }

                    if (respBearer.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                    {
                        if (attempt == maxAttempts)
                            throw new InvalidOperationException("WB Promotion calendar nomenclatures вернул 429 слишком много раз. Подожди 30-60 сек и повтори.");

                        await Task.Delay(GetRetryDelay(respBearer, attempt), ct);
                        continue;
                    }

                    throw new InvalidOperationException(BuildHttpErrorMessage("WB Promotion calendar nomenclatures", url, respBearer, payloadBearer));
                }

                if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    if (attempt == maxAttempts)
                        throw new InvalidOperationException("WB Promotion calendar nomenclatures вернул 429 слишком много раз. Подожди 30-60 сек и повтори.");

                    await Task.Delay(GetRetryDelay(resp, attempt), ct);
                    continue;
                }

                throw new InvalidOperationException(BuildHttpErrorMessage("WB Promotion calendar nomenclatures", url, resp, payload));
            }

            throw new InvalidOperationException("WB Promotion calendar nomenclatures: не удалось выполнить запрос (retry exhausted).");
        }

        private static (string start, string end) GetCalendarRange()
        {
            var start = DateTime.UtcNow.AddDays(-1);
            var end = DateTime.UtcNow.AddDays(60);
            return (start.ToString("yyyy-MM-ddTHH:mm:ssZ"), end.ToString("yyyy-MM-ddTHH:mm:ssZ"));
        }

        private static string BuildCalendarUrl(string path, long? nmId)
        {
            var (startDateTime, endDateTime) = GetCalendarRange();
            var start = Uri.EscapeDataString(startDateTime);
            var end = Uri.EscapeDataString(endDateTime);

            var baseQuery = $"startDateTime={start}&endDateTime={end}&allPromo=true&limit=1000&offset=0";
            if (nmId.HasValue)
                return $"{path}?nmId={nmId.Value}&{baseQuery}";

            return $"{path}?{baseQuery}";
        }

        private static string BuildCalendarNomenclaturesUrl(long promotionId, bool inAction)
        {
            return $"/api/v1/calendar/promotions/nomenclatures?promotionID={promotionId}&inAction={inAction.ToString().ToLowerInvariant()}&limit=1000&offset=0";
        }

        private async Task<List<WbPromotionCalendarItem>> LoadCalendarEntriesAsync(string baseUrl, string token, long nmId, CancellationToken ct)
        {
            var payload = await GetCalendarPromotionsRawAsync(baseUrl, token, ct);
            var promotions = ParseCalendarPromotions(payload);
            var result = new List<WbPromotionCalendarItem>();

            foreach (var promo in promotions)
            {
                if (string.Equals(promo.Type, "regular", StringComparison.OrdinalIgnoreCase))
                {
                    var found = await TryLoadNomenclatureAsync(baseUrl, token, promo.Id, nmId, ct);
                    if (found == null)
                        continue;

                    result.Add(new WbPromotionCalendarItem
                    {
                        PromotionId = promo.Id,
                        Name = promo.Name,
                        Status = promo.Type,
                        Participation = found.InAction ? "Да" : "Нет",
                        DateFrom = promo.StartDateTime ?? "",
                        DateTo = promo.EndDateTime ?? "",
                        Details = $"price={found.Price}; planPrice={found.PlanPrice}; discount={found.Discount}; planDiscount={found.PlanDiscount}"
                    });
                    continue;
                }

                if (string.Equals(promo.Type, "auto", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(new WbPromotionCalendarItem
                    {
                        PromotionId = promo.Id,
                        Name = promo.Name,
                        Status = promo.Type,
                        Participation = "н/д (auto)",
                        DateFrom = promo.StartDateTime ?? "",
                        DateTo = promo.EndDateTime ?? "",
                        Details = "auto promotion; WB API не даёт список товаров"
                    });
                }
            }

            return result;
        }

        private async Task<List<WbPromotionItem>> LoadPromotionsFromCalendarAsync(string baseUrl, string token, long nmId, CancellationToken ct)
        {
            var payload = await GetCalendarPromotionsRawAsync(baseUrl, token, ct);
            var promotions = ParseCalendarPromotions(payload);
            var result = new List<WbPromotionItem>();

            foreach (var promo in promotions)
            {
                if (!string.Equals(promo.Type, "regular", StringComparison.OrdinalIgnoreCase))
                    continue;

                var found = await TryLoadNomenclatureAsync(baseUrl, token, promo.Id, nmId, ct);
                if (found == null)
                    continue;

                result.Add(new WbPromotionItem
                {
                    PromotionId = promo.Id,
                    Name = promo.Name,
                    RequiredDiscount = found.PlanDiscount,
                    Status = found.InAction ? "В акции" : "Можно войти",
                    Details = $"price={found.Price}; planPrice={found.PlanPrice}; currentDiscount={found.Discount}; planDiscount={found.PlanDiscount}"
                });
            }

            return result;
        }

        private async Task<NomenclatureInfo?> TryLoadNomenclatureAsync(string baseUrl, string token, long promotionId, long nmId, CancellationToken ct)
        {
            var payload = await GetCalendarNomenclaturesRawAsync(baseUrl, token, promotionId, true, ct);
            var found = ParseNomenclatures(payload, nmId);
            if (found != null)
            {
                found.InAction = true;
                return found;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(650), ct);

            payload = await GetCalendarNomenclaturesRawAsync(baseUrl, token, promotionId, false, ct);
            found = ParseNomenclatures(payload, nmId);
            if (found != null)
            {
                found.InAction = false;
                return found;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(650), ct);
            return null;
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

        private static List<CalendarPromotion> ParseCalendarPromotions(string payload)
        {
            var result = new List<CalendarPromotion>();

            try
            {
                using var doc = JsonDocument.Parse(payload);
                ExtractCalendarPromotions(doc.RootElement, result);
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

        private static void ExtractCalendarPromotions(JsonElement element, List<CalendarPromotion> result)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    if (element.TryGetProperty("data", out var data))
                        ExtractCalendarPromotions(data, result);
                    if (element.TryGetProperty("promotions", out var promotions))
                        ExtractCalendarPromotions(promotions, result);
                    if (element.TryGetProperty("promotion", out var promotion))
                        ExtractCalendarPromotions(promotion, result);

                    if (TryGetInt64FromAny(element, "id", "promotionId", "actionId") is long id)
                    {
                        var name = TryGetStringFromAny(element, "name", "action_name", "title") ?? "Акция";
                        var type = TryGetStringFromAny(element, "type") ?? "";
                        var start = TryGetStringFromAny(element, "startDateTime", "date_from", "start", "from");
                        var end = TryGetStringFromAny(element, "endDateTime", "date_to", "end", "to");

                        result.Add(new CalendarPromotion
                        {
                            Id = id,
                            Name = name,
                            Type = type,
                            StartDateTime = start,
                            EndDateTime = end
                        });
                    }
                    else
                    {
                        foreach (var prop in element.EnumerateObject())
                            ExtractCalendarPromotions(prop.Value, result);
                    }
                    break;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                        ExtractCalendarPromotions(item, result);
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

        private static NomenclatureInfo? ParseNomenclatures(string payload, long nmId)
        {
            try
            {
                using var doc = JsonDocument.Parse(payload);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("data", out var data)
                    && data.TryGetProperty("nomenclatures", out var nms)
                    && nms.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in nms.EnumerateArray())
                    {
                        if (!item.TryGetProperty("id", out var idEl))
                            continue;

                        if (idEl.ValueKind == JsonValueKind.Number && idEl.TryGetInt64(out var id) && id == nmId)
                            return BuildNomenclatureInfo(item);
                        if (idEl.ValueKind == JsonValueKind.String && long.TryParse(idEl.GetString(), out var sid) && sid == nmId)
                            return BuildNomenclatureInfo(item);
                    }
                }
            }
            catch
            {
                return null;
            }

            return null;
        }

        private static NomenclatureInfo BuildNomenclatureInfo(JsonElement item)
        {
            return new NomenclatureInfo
            {
                Price = TryGetStringFromAny(item, "price") ?? "",
                PlanPrice = TryGetStringFromAny(item, "planPrice") ?? "",
                Discount = TryGetStringFromAny(item, "discount") ?? "",
                PlanDiscount = TryGetStringFromAny(item, "planDiscount") ?? ""
            };
        }

        private sealed class CalendarPromotion
        {
            public long Id { get; set; }
            public string Name { get; set; } = "";
            public string Type { get; set; } = "";
            public string? StartDateTime { get; set; }
            public string? EndDateTime { get; set; }
        }

        private sealed class NomenclatureInfo
        {
            public bool InAction { get; set; }
            public string Price { get; set; } = "";
            public string PlanPrice { get; set; } = "";
            public string Discount { get; set; } = "";
            public string PlanDiscount { get; set; } = "";
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

        private static string BuildHttpErrorMessage(string prefix, string url, HttpResponseMessage resp, string payload)
        {
            return $"{prefix} вернул {(int)resp.StatusCode} на {url}. {Shorten(payload)}";
        }

        private static string Shorten(string value, int max = 800)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "";

            return value.Length <= max ? value : value.Substring(0, max) + "...";
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

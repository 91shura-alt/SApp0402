using Microsoft.EntityFrameworkCore;
using SellerOps.App.Data;
using SellerOps.App.Domain;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ClosedXML.Excel;

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
            var existing = await _db.WbPromotionItems
                .Where(x => x.NmId == nmId && x.PromotionId != null)
                .ToListAsync(ct);
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

        public async Task<AutoPromotionImportResult> ImportAutoPromotionExcelAsync(string path, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException("Файл Excel не найден.", path);

            using var wb = new XLWorkbook(path);
            var ws = wb.Worksheets.First();

            var promotionName = BuildPromotionNameFromFile(path);

            var headers = BuildHeaderMap(ws);
            var requiredHeaders = new[]
            {
                "Артикул WB",
                "Загружаемая скидка для участия в акции, %",
                "Плановая цена для акции",
                "Текущая розничная цена",
                "Текущая скидка сайта, %",
                "Товар уже участвует в акции",
                "Статус"
            };

            var missing = requiredHeaders
                .Where(h => !headers.ContainsKey(NormalizeHeader(h)))
                .ToList();

            if (missing.Count > 0)
                throw new InvalidOperationException($"В Excel не найдены колонки: {string.Join(", ", missing)}");

            var colNmId = headers[NormalizeHeader("Артикул WB")];
            var colRequiredDiscount = headers[NormalizeHeader("Загружаемая скидка для участия в акции, %")];
            var colPlanPrice = headers[NormalizeHeader("Плановая цена для акции")];
            var colCurrentPrice = headers[NormalizeHeader("Текущая розничная цена")];
            var colCurrentSiteDiscount = headers[NormalizeHeader("Текущая скидка сайта, %")];
            var colParticipates = headers[NormalizeHeader("Товар уже участвует в акции")];
            var colStatus = headers[NormalizeHeader("Статус")];

            var importedAt = DateTime.UtcNow;
            var errors = new List<string>();
            var imported = 0;
            var skipped = 0;

            var oldRows = await _db.WbPromotionItems
                .Where(x => x.PromotionId == null && x.Name == promotionName)
                .ToListAsync(ct);
            if (oldRows.Count > 0)
                _db.WbPromotionItems.RemoveRange(oldRows);

            var row = 2;
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                var nmCell = ws.Cell(row, colNmId);
                if (nmCell.IsEmpty() && string.IsNullOrWhiteSpace(nmCell.GetString()))
                    break;

                if (!TryGetLong(nmCell, out var nmId))
                {
                    skipped++;
                    errors.Add($"Строка {row}: не удалось распарсить Артикул WB.");
                    row++;
                    continue;
                }

                var requiredDiscount = NormalizePercent(GetCellString(ws.Cell(row, colRequiredDiscount)));
                var planPrice = GetCellString(ws.Cell(row, colPlanPrice));
                var currentPrice = GetCellString(ws.Cell(row, colCurrentPrice));
                var currentSiteDiscount = NormalizePercent(GetCellString(ws.Cell(row, colCurrentSiteDiscount)));
                var participatesRaw = GetCellString(ws.Cell(row, colParticipates));
                var status = GetCellString(ws.Cell(row, colStatus));

                var inAction = IsTrueValue(participatesRaw);
                var details = $"planPrice={planPrice}; currentPrice={currentPrice}; currentSiteDiscount={currentSiteDiscount}; wbStatus={status}";

                var raw = JsonSerializer.Serialize(new Dictionary<string, string?>
                {
                    ["nmId"] = nmId.ToString(),
                    ["requiredDiscount"] = requiredDiscount,
                    ["planPrice"] = planPrice,
                    ["currentPrice"] = currentPrice,
                    ["currentSiteDiscount"] = currentSiteDiscount,
                    ["participates"] = participatesRaw,
                    ["status"] = status
                });

                _db.WbPromotionItems.Add(new WbPromotionItem
                {
                    PromotionId = null,
                    Name = promotionName,
                    NmId = nmId,
                    RequiredDiscount = requiredDiscount,
                    Status = inAction ? "Участвует" : "Не участвует",
                    Details = details,
                    RawJson = raw,
                    ImportedAtUtc = importedAt
                });

                imported++;
                row++;
            }

            await _db.SaveChangesAsync(ct);

            return new AutoPromotionImportResult
            {
                PromotionName = promotionName,
                Imported = imported,
                Skipped = skipped,
                Errors = errors
            };
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

        private const int CalendarPageSize = 1000;
        private static readonly TimeSpan CalendarThrottleDelay = TimeSpan.FromMilliseconds(300);

        private async Task<string> GetCalendarPromotionsPageAsync(string baseUrl, string token, int limit, int offset, CancellationToken ct)
        {
            const int maxAttempts = 6;
            var url = BuildCalendarPromotionsUrl(limit, offset);

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

        private async Task<string> GetPromotionNomenclaturesPageAsync(string baseUrl, string token, long promotionId, bool inAction, int limit, int offset, CancellationToken ct)
        {
            const int maxAttempts = 6;
            var url = BuildCalendarNomenclaturesUrl(promotionId, inAction, limit, offset);

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
            var start = DateTime.UtcNow.AddHours(-3);
            var end = DateTime.UtcNow.AddDays(30);
            return (start.ToString("yyyy-MM-ddTHH:mm:ssZ"), end.ToString("yyyy-MM-ddTHH:mm:ssZ"));
        }

        private static string BuildCalendarPromotionsUrl(int limit, int offset)
        {
            var (startDateTime, endDateTime) = GetCalendarRange();
            var start = Uri.EscapeDataString(startDateTime);
            var end = Uri.EscapeDataString(endDateTime);

            return $"/api/v1/calendar/promotions?startDateTime={start}&endDateTime={end}&allPromo=false&limit={limit}&offset={offset}";
        }

        private static string BuildCalendarNomenclaturesUrl(long promotionId, bool inAction, int limit, int offset)
        {
            return $"/api/v1/calendar/promotions/nomenclatures?promotionID={promotionId}&inAction={inAction.ToString().ToLowerInvariant()}&limit={limit}&offset={offset}";
        }

        private async Task<List<WbPromotionCalendarItem>> LoadCalendarEntriesAsync(string baseUrl, string token, long nmId, CancellationToken ct)
        {
            var promotions = await LoadCalendarPromotionsAsync(baseUrl, token, ct);
            var result = new List<WbPromotionCalendarItem>();
            var seenPromotions = new HashSet<long>();

            foreach (var promo in promotions)
            {
                if (!seenPromotions.Add(promo.Id))
                    continue;

                if (string.Equals(promo.Type, "regular", StringComparison.OrdinalIgnoreCase))
                {
                    var found = await TryLoadNomenclatureAsync(baseUrl, token, promo.Id, nmId, ct);
                    var participation = found?.InAction == true ? "Да" : "Нет";
                    var details = found == null
                        ? ""
                        : $"price={found.Price}; planPrice={found.PlanPrice}; discount={found.Discount}; planDiscount={found.PlanDiscount}";

                    result.Add(new WbPromotionCalendarItem
                    {
                        PromotionId = promo.Id,
                        Name = promo.Name,
                        Status = promo.Type,
                        Participation = participation,
                        DateFrom = promo.StartDateTime ?? "",
                        DateTo = promo.EndDateTime ?? "",
                        Details = details
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
            var promotions = await LoadCalendarPromotionsAsync(baseUrl, token, ct);
            var result = new List<WbPromotionItem>();
            var seenPromotions = new HashSet<long>();
            var sizePrices = await _db.WbPriceSizes.AsNoTracking()
                .Where(x => x.NmId == nmId && x.Price.HasValue)
                .Select(x => x.Price!.Value)
                .ToListAsync(ct);

            foreach (var promo in promotions)
            {
                if (!seenPromotions.Add(promo.Id))
                    continue;

                if (!string.Equals(promo.Type, "regular", StringComparison.OrdinalIgnoreCase))
                    continue;

                var found = await TryLoadNomenclatureAsync(baseUrl, token, promo.Id, nmId, ct);
                if (found == null)
                    continue;

                var details = $"price={found.Price}; planPrice={found.PlanPrice}; currentDiscount={found.Discount}; planDiscount={found.PlanDiscount}";
                var targetPriceInfo = BuildTargetPriceInfo(found, sizePrices);
                if (!string.IsNullOrWhiteSpace(targetPriceInfo))
                    details = $"{details}; {targetPriceInfo}";

                result.Add(new WbPromotionItem
                {
                    PromotionId = promo.Id,
                    Name = promo.Name,
                    RequiredDiscount = found.PlanDiscount,
                    Status = found.InAction ? "Участвует" : "Не участвует",
                    Details = details
                });
            }

            return result;
        }

        private async Task<NomenclatureInfo?> TryLoadNomenclatureAsync(string baseUrl, string token, long promotionId, long nmId, CancellationToken ct)
        {
            var found = await FindNomenclaturePagedAsync(baseUrl, token, promotionId, nmId, true, ct);
            if (found != null)
                return found;

            return await FindNomenclaturePagedAsync(baseUrl, token, promotionId, nmId, false, ct);
        }

        private async Task<NomenclatureInfo?> FindNomenclaturePagedAsync(string baseUrl, string token, long promotionId, long nmId, bool inAction, CancellationToken ct)
        {
            var offset = 0;

            while (true)
            {
                var payload = await GetPromotionNomenclaturesPageAsync(baseUrl, token, promotionId, inAction, CalendarPageSize, offset, ct);
                var page = ParseNomenclaturesPage(payload);
                if (page.Items.Count > 0)
                {
                    var match = page.Items.FirstOrDefault(item => item.Id == nmId);
                    if (match != null)
                    {
                        match.InAction = inAction;
                        return match;
                    }
                }

                if (page.Items.Count < CalendarPageSize)
                    return null;

                offset += CalendarPageSize;
                await Task.Delay(CalendarThrottleDelay, ct);
            }
        }

        private async Task<List<CalendarPromotion>> LoadCalendarPromotionsAsync(string baseUrl, string token, CancellationToken ct)
        {
            var offset = 0;
            var result = new List<CalendarPromotion>();

            while (true)
            {
                var payload = await GetCalendarPromotionsPageAsync(baseUrl, token, CalendarPageSize, offset, ct);
                var page = ParseCalendarPromotionsPage(payload);
                result.AddRange(page);

                if (page.Count < CalendarPageSize)
                    break;

                offset += CalendarPageSize;
                await Task.Delay(CalendarThrottleDelay, ct);
            }

            return result;
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

        private static List<CalendarPromotion> ParseCalendarPromotionsPage(string payload)
        {
            var result = new List<CalendarPromotion>();
            var seenIds = new HashSet<long>();

            try
            {
                using var doc = JsonDocument.Parse(payload);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("data", out var data)
                    && data.TryGetProperty("promotions", out var promotions)
                    && promotions.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in promotions.EnumerateArray())
                    {
                        var promotion = ParsePromotion(item);
                        if (promotion != null && seenIds.Add(promotion.Id))
                            result.Add(promotion);
                    }
                }
            }
            catch
            {
                // игнор — вернём пустой список
            }

            return result;
        }


        private static CalendarPromotion? ParsePromotion(JsonElement item)
        {
            if (item.ValueKind != JsonValueKind.Object)
                return null;

            if (!item.TryGetProperty("id", out var idEl))
                return null;

            var id = ParseLong(idEl);
            if (!id.HasValue)
                return null;

            if (!item.TryGetProperty("name", out var nameEl))
                return null;

            var name = nameEl.GetString();
            if (string.IsNullOrWhiteSpace(name))
                return null;

            var type = item.TryGetProperty("type", out var typeEl) ? typeEl.GetString() ?? "" : "";
            var start = item.TryGetProperty("startDateTime", out var startEl) ? startEl.GetString() : null;
            var end = item.TryGetProperty("endDateTime", out var endEl) ? endEl.GetString() : null;

            return new CalendarPromotion
            {
                Id = id.Value,
                Name = name,
                Type = type,
                StartDateTime = start,
                EndDateTime = end
            };
        }

        private static NomenclaturesPage ParseNomenclaturesPage(string payload)
        {
            var result = new List<NomenclatureInfo>();
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
                        var info = ParseNomenclature(item);
                        if (info != null)
                            result.Add(info);
                    }
                }
            }
            catch
            {
                return new NomenclaturesPage(result);
            }

            return new NomenclaturesPage(result);
        }

        private static NomenclatureInfo? ParseNomenclature(JsonElement item)
        {
            if (item.ValueKind != JsonValueKind.Object)
                return null;

            if (!item.TryGetProperty("id", out var idEl))
                return null;

            var id = ParseLong(idEl);
            if (!id.HasValue)
                return null;

            return new NomenclatureInfo
            {
                Id = id.Value,
                Price = GetJsonValue(item, "price"),
                PlanPrice = GetJsonValue(item, "planPrice"),
                Discount = GetJsonValue(item, "discount"),
                PlanDiscount = GetJsonValue(item, "planDiscount")
            };
        }

        private static string GetJsonValue(JsonElement item, string name)
        {
            if (!item.TryGetProperty(name, out var value))
                return "";

            return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString();
        }

        private static string BuildTargetPriceInfo(NomenclatureInfo info, List<decimal> sizePrices)
        {
            if (!TryParsePercent(info.PlanDiscount, out var percent))
                return "";

            if (sizePrices.Count == 0)
            {
                if (string.IsNullOrWhiteSpace(info.PlanPrice))
                    return "";

                return $"targetPrice={info.PlanPrice}";
            }

            var multiplier = (100m - percent) / 100m;
            var targets = sizePrices.Select(price => price * multiplier).ToList();
            var min = targets.Min();
            var max = targets.Max();
            if (min == max)
                return $"targetPrice≈{min:0.##}";

            return $"targetPrice≈{min:0.##}-{max:0.##}";
        }

        private static bool TryParsePercent(string value, out decimal percent)
        {
            percent = 0m;
            if (string.IsNullOrWhiteSpace(value))
                return false;

            var cleaned = value.Replace("%", "").Trim();
            if (decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out percent))
                return true;
            if (decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.CurrentCulture, out percent))
                return true;

            return false;
        }

        private static Dictionary<string, int> BuildHeaderMap(IXLWorksheet ws)
        {
            var map = new Dictionary<string, int>();
            var col = 1;
            while (true)
            {
                var header = ws.Cell(1, col).GetString();
                if (string.IsNullOrWhiteSpace(header))
                    break;

                var key = NormalizeHeader(header);
                if (!map.ContainsKey(key))
                    map[key] = col;

                col++;
                if (col > 200)
                    break;
            }

            return map;
        }

        private static string NormalizeHeader(string header)
        {
            var normalized = header.Replace('\u00A0', ' ').Trim();
            var parts = normalized.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return string.Join(' ', parts).ToLowerInvariant();
        }

        private static string GetCellString(IXLCell cell)
        {
            if (cell.TryGetValue<decimal>(out var decimalValue))
                return decimalValue.ToString("0.##", CultureInfo.InvariantCulture);
            if (cell.TryGetValue<double>(out var doubleValue))
                return doubleValue.ToString("0.##", CultureInfo.InvariantCulture);
            var s = cell.GetString();
            return string.IsNullOrWhiteSpace(s) ? "" : s.Trim();
        }

        private static bool TryGetLong(IXLCell cell, out long value)
        {
            value = 0;
            if (cell.TryGetValue<long>(out var longValue))
            {
                value = longValue;
                return true;
            }

            var s = cell.GetString();
            return long.TryParse(s?.Trim(), out value);
        }

        private static bool IsTrueValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            var normalized = value.Trim();
            return string.Equals(normalized, "да", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "yes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "1", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizePercent(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "";

            return value.Replace("%", "").Trim();
        }

        private static string BuildPromotionNameFromFile(string path)
        {
            var name = Path.GetFileNameWithoutExtension(path) ?? "";
            name = name.Trim();

            const string prefix = "Все товары подходящие для акции_";
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                name = name.Substring(prefix.Length);

            name = Regex.Replace(name, @"_\d{2}\.\d{2}\.\d{4}\s\d{2}\.\d{2}\.\d{2}$", "");
            name = name.Replace('_', ' ');
            name = Regex.Replace(name, @"\s+", " ").Trim();

            return string.IsNullOrWhiteSpace(name) ? "Автоакция" : name;
        }

        public sealed class AutoPromotionImportResult
        {
            public string PromotionName { get; set; } = "";
            public int Imported { get; set; }
            public int Skipped { get; set; }
            public List<string> Errors { get; set; } = new();
        }

        private static long? ParseLong(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var v))
                return v;
            if (element.ValueKind == JsonValueKind.String && long.TryParse(element.GetString(), out var s))
                return s;

            return null;
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
            public long Id { get; set; }
            public bool InAction { get; set; }
            public string Price { get; set; } = "";
            public string PlanPrice { get; set; } = "";
            public string Discount { get; set; } = "";
            public string PlanDiscount { get; set; } = "";
        }

        private sealed class NomenclaturesPage
        {
            public NomenclaturesPage(List<NomenclatureInfo> items)
            {
                Items = items;
            }

            public List<NomenclatureInfo> Items { get; }
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

    }
}

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

        public async Task<int> RefreshPromotionsForNmIdAsync(long nmId, CancellationToken ct = default)
        {
            var (token, baseUrl) = GetCreds();
            var payload = await GetPromotionsRawAsync(baseUrl, token, ct);

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

        private async Task<string> GetPromotionsRawAsync(string baseUrl, string token, CancellationToken ct)
        {
            using var http = _http.Create(baseUrl, token, bearerHeader: false);
            using var resp = await http.PostAsync("/adv/v1/promotion/adverts", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"), ct);
            var payload = await resp.Content.ReadAsStringAsync(ct);

            if (resp.IsSuccessStatusCode)
                return payload;

            if ((int)resp.StatusCode == 401)
            {
                using var httpBearer = _http.Create(baseUrl, token, bearerHeader: true);
                using var respBearer = await httpBearer.PostAsync("/adv/v1/promotion/adverts", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"), ct);
                var payloadBearer = await respBearer.Content.ReadAsStringAsync(ct);

                if (respBearer.IsSuccessStatusCode)
                    return payloadBearer;

                throw new InvalidOperationException($"WB Promotion adverts вернул {(int)respBearer.StatusCode}. {payloadBearer}");
            }

            throw new InvalidOperationException($"WB Promotion adverts вернул {(int)resp.StatusCode}. {payload}");
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

        private static void ExtractPromotions(JsonElement element, long nmId, List<WbPromotionItem> result)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    if (ContainsNm(element, nmId))
                    {
                        var name = TryGetStringFromAny(element, "name", "title") ?? "Акция";
                        var required = TryGetStringFromAny(element, "requiredDiscount", "minDiscount", "discount", "discountPercent") ?? "";
                        var status = TryGetStringFromAny(element, "status", "state") ?? "";
                        var details = TryGetStringFromAny(element, "comment", "details", "description") ?? "";
                        var promoId = TryGetInt64FromAny(element, "id", "promotionId", "advertId");

                        result.Add(new WbPromotionItem
                        {
                            PromotionId = promoId,
                            Name = name,
                            RequiredDiscount = required,
                            Status = status,
                            Details = details,
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

        private static bool ContainsNm(JsonElement element, long nmId)
        {
            if (TryGetInt64FromAny(element, "nmId", "nm", "nmID") == nmId)
                return true;

            if (element.TryGetProperty("nms", out var nms) && nms.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in nms.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var v) && v == nmId)
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

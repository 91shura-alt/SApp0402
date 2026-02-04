using SellerOps.App.Data;
using SellerOps.App.Domain;
using SellerOps.App.Services;
using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace SellerOps.App.Views
{
    public partial class WbSettingsPage : Page
    {
        public WbSettingsPage()
        {
            InitializeComponent();
            LoadTokens();
        }

        private void LoadTokens()
        {
            try
            {
                TokensGrid.ItemsSource = AppDbContext.Instance.ApiTokens
                    .OrderByDescending(x => x.Id)
                    .ToList();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Ошибка загрузки токенов",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            if (NavigationService?.CanGoBack == true) NavigationService.GoBack();
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var category = (CategoryBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Content";
                var sandbox = SandboxBox.IsChecked == true;

                var alias = (AliasBox.Text ?? "").Trim();
                var token = (TokenBox.Password ?? "").Trim();

                long? supplierId = null;
                var rawSupplier = (SupplierIdBox.Text ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(rawSupplier) && long.TryParse(rawSupplier, out var sid))
                    supplierId = sid;

                if (string.IsNullOrWhiteSpace(token))
                {
                    MessageBox.Show("Введите API-токен.", "WB API", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var enc = SecureStorage.Protect(token);

                var model = new ApiToken
                {
                    Category = category,
                    Alias = string.IsNullOrWhiteSpace(alias) ? null : alias,
                    IsSandbox = sandbox,
                    EncryptedToken = enc,
                    CreatedAtUtc = DateTime.UtcNow,
                    SupplierId = supplierId
                };

                var db = AppDbContext.Instance;
                db.ApiTokens.Add(model);
                db.SaveChanges();

                LoadTokens();
                MessageBox.Show("Токен сохранён.", "WB API", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Ошибка сохранения", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Проверяем реальным контент-методом (list 1 карточка), а не /ping.
        /// Пробуем сперва Authorization: <token>, затем — Bearer fallback.
        /// Если выделена строка в таблице — проверяем ЕЁ токен; иначе — из полей сверху.
        /// </summary>
        private async void Test_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string category = (CategoryBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Content";
                bool sandbox = SandboxBox.IsChecked == true;

                string token;
                long? supplierId = null;

                if (TokensGrid.SelectedItem is ApiToken row)
                {
                    token = SecureStorage.Unprotect(row.EncryptedToken);
                    category = row.Category;
                    sandbox = row.IsSandbox;
                    supplierId = row.SupplierId;
                }
                else
                {
                    token = (TokenBox.Password ?? "").Trim();
                    var rawSupplier = (SupplierIdBox.Text ?? "").Trim();
                    if (!string.IsNullOrWhiteSpace(rawSupplier) && long.TryParse(rawSupplier, out var sid))
                        supplierId = sid;
                }

                if (string.IsNullOrWhiteSpace(token))
                {
                    MessageBox.Show("Введите токен или выберите строку в таблице.");
                    return;
                }

                string baseUrl = category switch
                {
                    "Content" => sandbox ? "https://content-api-sandbox.wildberries.ru" : "https://content-api.wildberries.ru",
                    "Statistics" => sandbox ? "https://statistics-api-sandbox.wildberries.ru" : "https://statistics-api.wildberries.ru",
                    "Analytics" => "https://seller-analytics-api.wildberries.ru",

                    "Marketplace" => "https://marketplace-api.wildberries.ru",
                    "PricesAndDiscounts" => sandbox ? "https://discounts-prices-api-sandbox.wildberries.ru" : "https://discounts-prices-api.wildberries.ru",

                    "Promotion" => sandbox ? "https://advert-api-sandbox.wildberries.ru" : "https://advert-api.wildberries.ru",
                    "Feedbacks" => sandbox ? "https://feedbacks-api-sandbox.wildberries.ru" : "https://feedbacks-api.wildberries.ru",
                    "BuyerChat" => "https://buyer-chat-api.wildberries.ru",
                    "Supplies" => "https://supplies-api.wildberries.ru",
                    "Returns" => "https://returns-api.wildberries.ru",

                    "Documents" => "https://documents-api.wildberries.ru",
                    "Finance" => "https://finance-api.wildberries.ru",

                    "Users" => "https://user-management-api.wildberries.ru",
                    "Common" => "https://common-api.wildberries.ru",

                    _ => "https://common-api.wildberries.ru",
                };

                if (category == "Content")
                {
                    // Тест-запрос: получить 1 карточку (без X-Supplier-ID!)
                    var body = new
                    {
                        settings = new
                        {
                            cursor = new { limit = 1 },
                            filter = new { withPhoto = -1 }
                        }
                    };
                    string json = System.Text.Json.JsonSerializer.Serialize(body);

                    // Authorization: <token>
                    using (var http = new HttpClient { BaseAddress = new Uri(baseUrl) })
                    {
                        http.DefaultRequestHeaders.Clear();
                        http.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
                        http.DefaultRequestHeaders.Add("Authorization", token);

                        var resp = await http.PostAsync("/content/v2/get/cards/list",
                            new StringContent(json, Encoding.UTF8, "application/json"));

                        if (resp.IsSuccessStatusCode)
                        {
                            MessageBox.Show("OK: токен валиден (Content).", "Проверка", MessageBoxButton.OK, MessageBoxImage.Information);
                            return;
                        }
                        if ((int)resp.StatusCode != 401)
                        {
                            var text = await resp.Content.ReadAsStringAsync();
                            MessageBox.Show($"Ошибка: {(int)resp.StatusCode} {resp.ReasonPhrase}\n{text}", "Проверка", MessageBoxButton.OK, MessageBoxImage.Error);
                            return;
                        }
                    }

                    // Фолбэк: Bearer
                    using (var http2 = new HttpClient { BaseAddress = new Uri(baseUrl) })
                    {
                        http2.DefaultRequestHeaders.Clear();
                        http2.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
                        http2.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

                        var resp2 = await http2.PostAsync("/content/v2/get/cards/list",
                            new StringContent(json, Encoding.UTF8, "application/json"));

                        if (resp2.IsSuccessStatusCode)
                        {
                            MessageBox.Show("OK: токен валиден (Content/Bearer).", "Проверка", MessageBoxButton.OK, MessageBoxImage.Information);
                            return;
                        }

                        var text2 = await resp2.Content.ReadAsStringAsync();
                        MessageBox.Show($"Ошибка: {(int)resp2.StatusCode} {resp2.ReasonPhrase}\n{text2}\n" +
                                        $"Проверьте, что токен категории Content и он не отозван.",
                                        "Проверка", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                }
                else
                {
                    // Для прочих категорий допустим X-Supplier-ID
                    using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
                    http.DefaultRequestHeaders.Clear();
                    http.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
                    http.DefaultRequestHeaders.Add("Authorization", token);
                    if (supplierId is > 0) http.DefaultRequestHeaders.Add("X-Supplier-ID", supplierId.Value.ToString());

                    var resp = await http.GetAsync("/ping");
                    if ((int)resp.StatusCode == 404)
                    {
                        MessageBox.Show($"OK: сервер отвечает, но /ping не реализован (404)\nURL: {baseUrl}");
                        return;
                    }
                    if (resp.IsSuccessStatusCode)
                    {
                        MessageBox.Show($"OK: {(int)resp.StatusCode} {resp.ReasonPhrase}\nURL: {baseUrl}");
                        return;
                    }
                    MessageBox.Show($"Ошибка: {(int)resp.StatusCode} {resp.ReasonPhrase}\nURL: {baseUrl}",
                        "Проверка", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }


        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (TokensGrid.SelectedItem is not ApiToken row)
                {
                    MessageBox.Show("Выберите строку с токеном в таблице.");
                    return;
                }
                var ask = MessageBox.Show(
                    $"Удалить токен Id={row.Id} (Category={row.Category}, Sandbox={row.IsSandbox})?",
                    "Удаление токена", MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (ask != MessageBoxResult.Yes) return;

                var db = AppDbContext.Instance;
                db.ApiTokens.Remove(row);
                db.SaveChanges();

                LoadTokens();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Ошибка удаления", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}

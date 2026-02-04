using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace SellerOps.App.Services
{
    public class WbHttpClientFactory
    {
        public HttpClient Create(string baseUrl, string token, bool bearerHeader = true, TimeSpan? timeout = null)
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip
                                       | DecompressionMethods.Deflate
                                       | DecompressionMethods.Brotli
            };

            var http = new HttpClient(handler)
            {
                BaseAddress = new Uri(baseUrl),
                Timeout = timeout ?? TimeSpan.FromMinutes(10) // было 100 секунд по умолчанию
            };

            http.DefaultRequestHeaders.Accept.Clear();
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            if (bearerHeader)
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            else
                http.DefaultRequestHeaders.Add("Authorization", token);

            return http;
        }
    }
}

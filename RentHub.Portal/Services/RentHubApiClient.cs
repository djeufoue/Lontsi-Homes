using Microsoft.AspNetCore.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace RentHub.Portal.Services
{
    
    public class RentHubApiClient
    {
        private readonly HttpClient _http;
        private readonly IHttpContextAccessor _ctx;

        public RentHubApiClient(HttpClient http, IHttpContextAccessor ctx)
        {
            _http = http;
            _ctx = ctx;
        }

        private void AttachBearer()
        {
            var token = _ctx.HttpContext?.Session.GetString("JWT_TOKEN");
            _http.DefaultRequestHeaders.Authorization = null;

            if (!string.IsNullOrWhiteSpace(token))
                _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        private static StringContent JsonBody<T>(T model)
            => new StringContent(JsonSerializer.Serialize(model), Encoding.UTF8, "application/json");

        private static JsonSerializerOptions JsonOpt => new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        public async Task<T> GetAsync<T>(string url)
        {
            AttachBearer();
            var res = await _http.GetAsync(url);
            var json = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) throw new Exception(json);
            return JsonSerializer.Deserialize<T>(json, JsonOpt)!;
        }

        public async Task<TOut> PostAsync<TIn, TOut>(string url, TIn body)
        {
            AttachBearer();
            var res = await _http.PostAsync(url, JsonBody(body));
            var json = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) throw new Exception(json);
            return JsonSerializer.Deserialize<TOut>(json, JsonOpt)!;
        }

        public async Task PostAsync<TIn>(string url, TIn body)
        {
            AttachBearer();
            var res = await _http.PostAsync(url, JsonBody(body));
            var json = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) throw new Exception(json);
        }

        public async Task PutAsync<TIn>(string url, TIn body)
        {
            AttachBearer();
            var res = await _http.PutAsync(url, JsonBody(body));
            var json = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) throw new Exception(json);
        }

        public async Task DeleteAsync(string url)
        {
            AttachBearer();
            var res = await _http.DeleteAsync(url);
            var json = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) throw new Exception(json);
        }

        public async Task<TOut> PostMultipartAsync<TOut>(string url, MultipartFormDataContent content)
        {
            AttachBearer();
            var res = await _http.PostAsync(url, content);
            var json = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) throw new Exception(json);
            return JsonSerializer.Deserialize<TOut>(json, JsonOpt)!;
        }
    }

}

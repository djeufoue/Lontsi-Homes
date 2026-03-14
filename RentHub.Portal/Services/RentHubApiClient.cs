using Microsoft.AspNetCore.Http;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace RentHub.Portal.Services
{
    public class RentHubApiClient
    {
        private const string JwtTokenClaimType = "jwt_token";

        private readonly HttpClient _http;
        private readonly IHttpContextAccessor _ctx;

        public RentHubApiClient(HttpClient http, IHttpContextAccessor ctx)
        {
            _http = http;
            _ctx = ctx;
        }

        private void AttachBearer()
        {
            var httpContext = _ctx.HttpContext;
            var token = httpContext?.Session.GetString("JWT_TOKEN");

            if (string.IsNullOrWhiteSpace(token) && httpContext?.User?.Identity?.IsAuthenticated == true)
            {
                token = httpContext.User.FindFirst(JwtTokenClaimType)?.Value;
                if (!string.IsNullOrWhiteSpace(token))
                {
                    httpContext.Session.SetString("JWT_TOKEN", token);
                }
            }

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

        private static Exception BuildApiException(HttpStatusCode statusCode, string raw)
        {
            if (statusCode == HttpStatusCode.Unauthorized || statusCode == HttpStatusCode.Forbidden)
            {
                return new Exception("{\"Code\":\"AUTH_SESSION_EXPIRED\",\"Message\":\"Your session expired. Please sign in again.\"}");
            }

            if (string.IsNullOrWhiteSpace(raw))
            {
                return new Exception("{\"Code\":\"API_REQUEST_FAILED\",\"Message\":\"Request failed.\"}");
            }

            return new Exception(raw);
        }

        public async Task<T> GetAsync<T>(string url)
        {
            AttachBearer();
            var res = await _http.GetAsync(url);
            var json = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) throw BuildApiException(res.StatusCode, json);
            return JsonSerializer.Deserialize<T>(json, JsonOpt)!;
        }

        public async Task<TOut> PostAsync<TIn, TOut>(string url, TIn body)
        {
            AttachBearer();
            var res = await _http.PostAsync(url, JsonBody(body));
            var json = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) throw BuildApiException(res.StatusCode, json);
            return JsonSerializer.Deserialize<TOut>(json, JsonOpt)!;
        }

        public async Task PostAsync<TIn>(string url, TIn body)
        {
            AttachBearer();
            var res = await _http.PostAsync(url, JsonBody(body));
            var json = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) throw BuildApiException(res.StatusCode, json);
        }

        public async Task PutAsync<TIn>(string url, TIn body)
        {
            AttachBearer();
            var res = await _http.PutAsync(url, JsonBody(body));
            var json = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) throw BuildApiException(res.StatusCode, json);
        }

        public async Task DeleteAsync(string url)
        {
            AttachBearer();
            var res = await _http.DeleteAsync(url);
            var json = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) throw BuildApiException(res.StatusCode, json);
        }

        public async Task<TOut> PostMultipartAsync<TOut>(string url, MultipartFormDataContent content)
        {
            AttachBearer();
            var res = await _http.PostAsync(url, content);
            var json = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) throw BuildApiException(res.StatusCode, json);
            return JsonSerializer.Deserialize<TOut>(json, JsonOpt)!;
        }
    }
}

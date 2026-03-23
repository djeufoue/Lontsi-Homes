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
        private readonly PortalAuthSessionService _authSession;

        public RentHubApiClient(HttpClient http, PortalAuthSessionService authSession)
        {
            _http = http;
            _authSession = authSession;
        }

        private async Task AttachBearerAsync()
        {
            var token = _authSession.GetCurrentToken();
            if (!string.IsNullOrWhiteSpace(token) && _authSession.IsTokenExpiringSoon(token, TimeSpan.FromMinutes(5)))
            {
                var refreshedToken = await TryRefreshTokenAsync(token);
                if (!string.IsNullOrWhiteSpace(refreshedToken))
                {
                    token = refreshedToken;
                    await _authSession.PersistTokenAsync(token);
                }
            }

            _http.DefaultRequestHeaders.Authorization = null;

            if (!string.IsNullOrWhiteSpace(token))
            {
                _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
        }

        private async Task<string?> TryRefreshTokenAsync(string currentToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "Account/refresh")
            {
                Content = JsonBody(new { })
            };

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", currentToken);

            using var response = await _http.SendAsync(request);
            var payload = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(payload))
            {
                return null;
            }

            try
            {
                using var doc = JsonDocument.Parse(payload);
                if (TryGetPropertyIgnoreCase(doc.RootElement, "token", out var tokenElement))
                {
                    return tokenElement.GetString();
                }
            }
            catch
            {
                return null;
            }

            return null;
        }

        private static StringContent JsonBody<T>(T model)
            => new StringContent(JsonSerializer.Serialize(model), Encoding.UTF8, "application/json");

        private static JsonSerializerOptions JsonOpt => new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                    {
                        value = property.Value;
                        return true;
                    }
                }
            }

            value = default;
            return false;
        }

        private static Exception BuildApiException(HttpStatusCode statusCode, string raw)
        {
            if (statusCode == HttpStatusCode.Unauthorized)
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
            await AttachBearerAsync();
            var res = await _http.GetAsync(url);
            var json = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) throw BuildApiException(res.StatusCode, json);
            return JsonSerializer.Deserialize<T>(json, JsonOpt)!;
        }

        public async Task<TOut> PostAsync<TIn, TOut>(string url, TIn body)
        {
            await AttachBearerAsync();
            var res = await _http.PostAsync(url, JsonBody(body));
            var json = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) throw BuildApiException(res.StatusCode, json);
            return JsonSerializer.Deserialize<TOut>(json, JsonOpt)!;
        }

        public async Task PostAsync<TIn>(string url, TIn body)
        {
            await AttachBearerAsync();
            var res = await _http.PostAsync(url, JsonBody(body));
            var json = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) throw BuildApiException(res.StatusCode, json);
        }

        public async Task PutAsync<TIn>(string url, TIn body)
        {
            await AttachBearerAsync();
            var res = await _http.PutAsync(url, JsonBody(body));
            var json = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) throw BuildApiException(res.StatusCode, json);
        }

        public async Task DeleteAsync(string url)
        {
            await AttachBearerAsync();
            var res = await _http.DeleteAsync(url);
            var json = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) throw BuildApiException(res.StatusCode, json);
        }

        public async Task<TOut> PostMultipartAsync<TOut>(string url, MultipartFormDataContent content)
        {
            await AttachBearerAsync();
            var res = await _http.PostAsync(url, content);
            var json = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) throw BuildApiException(res.StatusCode, json);
            return JsonSerializer.Deserialize<TOut>(json, JsonOpt)!;
        }

        public async Task<string?> RefreshTokenAsync()
        {
            var currentToken = _authSession.GetCurrentToken();
            if (string.IsNullOrWhiteSpace(currentToken))
            {
                return null;
            }

            var refreshedToken = await TryRefreshTokenAsync(currentToken);
            if (!string.IsNullOrWhiteSpace(refreshedToken))
            {
                await _authSession.PersistTokenAsync(refreshedToken);
            }

            return refreshedToken;
        }
    }
}

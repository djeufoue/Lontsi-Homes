using Microsoft.AspNetCore.Http;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace RentHub.Portal.Services
{
    public record ApiFileResult(byte[] Bytes, string ContentType, string FileName);

    public class RentHubApiClient
    {
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

        private async Task PrepareAuthorizationAsync(bool attachBearer)
        {
            if (attachBearer)
            {
                await AttachBearerAsync();
                return;
            }

            _http.DefaultRequestHeaders.Authorization = null;
        }

        private static Exception BuildApiException(HttpStatusCode statusCode, string raw, bool treatUnauthorizedAsSessionExpired)
        {
            if (statusCode == HttpStatusCode.Unauthorized &&
                treatUnauthorizedAsSessionExpired &&
                string.IsNullOrWhiteSpace(raw))
            {
                return new Exception("{\"Code\":\"AUTH_SESSION_EXPIRED\",\"Message\":\"Your session expired. Please sign in again.\"}");
            }

            if (string.IsNullOrWhiteSpace(raw))
            {
                return new Exception("{\"Code\":\"API_REQUEST_FAILED\",\"Message\":\"Request failed.\"}");
            }

            return new Exception(raw);
        }

        private async Task<T> GetAsyncCore<T>(string url, bool attachBearer, bool treatUnauthorizedAsSessionExpired)
        {
            await PrepareAuthorizationAsync(attachBearer);
            var res = await _http.GetAsync(url);
            var json = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) throw BuildApiException(res.StatusCode, json, treatUnauthorizedAsSessionExpired);
            return JsonSerializer.Deserialize<T>(json, JsonOpt)!;
        }

        private async Task<TOut> PostAsyncCore<TIn, TOut>(string url, TIn body, bool attachBearer, bool treatUnauthorizedAsSessionExpired)
        {
            await PrepareAuthorizationAsync(attachBearer);
            var res = await _http.PostAsync(url, JsonBody(body));
            var json = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) throw BuildApiException(res.StatusCode, json, treatUnauthorizedAsSessionExpired);
            return JsonSerializer.Deserialize<TOut>(json, JsonOpt)!;
        }

        private async Task PostAsyncCore<TIn>(string url, TIn body, bool attachBearer, bool treatUnauthorizedAsSessionExpired)
        {
            await PrepareAuthorizationAsync(attachBearer);
            var res = await _http.PostAsync(url, JsonBody(body));
            var json = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) throw BuildApiException(res.StatusCode, json, treatUnauthorizedAsSessionExpired);
        }

        public async Task<T> GetAsync<T>(string url)
        {
            return await GetAsyncCore<T>(url, attachBearer: true, treatUnauthorizedAsSessionExpired: true);
        }

        public async Task<T> GetAnonymousAsync<T>(string url)
        {
            return await GetAsyncCore<T>(url, attachBearer: false, treatUnauthorizedAsSessionExpired: false);
        }

        public async Task<TOut> PostAsync<TIn, TOut>(string url, TIn body)
        {
            return await PostAsyncCore<TIn, TOut>(url, body, attachBearer: true, treatUnauthorizedAsSessionExpired: true);
        }

        public async Task<TOut> PostAnonymousAsync<TIn, TOut>(string url, TIn body)
        {
            return await PostAsyncCore<TIn, TOut>(url, body, attachBearer: false, treatUnauthorizedAsSessionExpired: false);
        }

        public async Task PostAsync<TIn>(string url, TIn body)
        {
            await PostAsyncCore(url, body, attachBearer: true, treatUnauthorizedAsSessionExpired: true);
        }

        public async Task PostAnonymousAsync<TIn>(string url, TIn body)
        {
            await PostAsyncCore(url, body, attachBearer: false, treatUnauthorizedAsSessionExpired: false);
        }

        public async Task PutAsync<TIn>(string url, TIn body)
        {
            await AttachBearerAsync();
            var res = await _http.PutAsync(url, JsonBody(body));
            var json = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) throw BuildApiException(res.StatusCode, json, treatUnauthorizedAsSessionExpired: true);
        }

        public async Task DeleteAsync(string url)
        {
            await AttachBearerAsync();
            var res = await _http.DeleteAsync(url);
            var json = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) throw BuildApiException(res.StatusCode, json, treatUnauthorizedAsSessionExpired: true);
        }

        public async Task<TOut> PostMultipartAsync<TOut>(string url, MultipartFormDataContent content)
        {
            await AttachBearerAsync();
            var res = await _http.PostAsync(url, content);
            var json = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) throw BuildApiException(res.StatusCode, json, treatUnauthorizedAsSessionExpired: true);
            return JsonSerializer.Deserialize<TOut>(json, JsonOpt)!;
        }

        public async Task<TOut> PostAnonymousMultipartAsync<TOut>(string url, MultipartFormDataContent content)
        {
            await PrepareAuthorizationAsync(attachBearer: false);
            var res = await _http.PostAsync(url, content);
            var json = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) throw BuildApiException(res.StatusCode, json, treatUnauthorizedAsSessionExpired: false);
            return JsonSerializer.Deserialize<TOut>(json, JsonOpt)!;
        }

        public async Task<ApiFileResult> GetFileAsync(string url)
        {
            await AttachBearerAsync();
            var res = await _http.GetAsync(url);
            if (!res.IsSuccessStatusCode)
            {
                var raw = await res.Content.ReadAsStringAsync();
                throw BuildApiException(res.StatusCode, raw, treatUnauthorizedAsSessionExpired: true);
            }

            var bytes = await res.Content.ReadAsByteArrayAsync();
            var contentType = res.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            var fileName = res.Content.Headers.ContentDisposition?.FileNameStar ??
                           res.Content.Headers.ContentDisposition?.FileName?.Trim('"') ??
                           "kyc-file";

            return new ApiFileResult(bytes, contentType, fileName);
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

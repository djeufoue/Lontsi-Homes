using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace RentHub.Portal.Middleware
{
    public class RequestUserLoggingMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<RequestUserLoggingMiddleware> _logger;

        public RequestUserLoggingMiddleware(RequestDelegate next, ILogger<RequestUserLoggingMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var userEmail = ResolveUserEmail(context.User);
            var userId = ResolveUserId(context.User);
            var stopwatch = Stopwatch.StartNew();

            using var scope = _logger.BeginScope(new Dictionary<string, object?>
            {
                ["UserEmail"] = userEmail,
                ["UserId"] = userId,
                ["TraceId"] = context.TraceIdentifier,
                ["RequestMethod"] = context.Request.Method,
                ["RequestPath"] = context.Request.Path.Value
            });

            _logger.LogInformation(
                "Portal request started {Method} {Path} by {UserEmail} ({UserId})",
                context.Request.Method,
                context.Request.Path,
                userEmail,
                userId);

            try
            {
                await _next(context);
            }
            finally
            {
                stopwatch.Stop();
                _logger.LogInformation(
                    "Portal request completed {Method} {Path} with {StatusCode} in {ElapsedMilliseconds}ms by {UserEmail} ({UserId})",
                    context.Request.Method,
                    context.Request.Path,
                    context.Response.StatusCode,
                    stopwatch.ElapsedMilliseconds,
                    userEmail,
                    userId);
            }
        }

        private static string ResolveUserEmail(ClaimsPrincipal user)
        {
            return user.FindFirstValue(ClaimTypes.Email)
                ?? user.FindFirstValue(JwtRegisteredClaimNames.Email)
                ?? user.FindFirstValue("email")
                ?? "anonymous";
        }

        private static string ResolveUserId(ClaimsPrincipal user)
        {
            return user.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? user.FindFirstValue(JwtRegisteredClaimNames.Sub)
                ?? user.FindFirstValue("sub")
                ?? "anonymous";
        }
    }
}

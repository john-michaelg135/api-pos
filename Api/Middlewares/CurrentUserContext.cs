using Microsoft.AspNetCore.Http;
using System.Text.Json;
using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;

namespace Api.Middlewares;

public class CurrentUserContext
{
    public Guid Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string[] Apps { get; set; } = Array.Empty<string>();
    public int? LocationId { get; set; }
    public string? SubRole { get; set; }
}

public static class HttpContextExtensions
{
    public static CurrentUserContext? GetCurrentUser(this HttpContext context)
    {
        if (context == null) return null;

        // 1. Try to read from Items first (populated by middleware if enabled)
        if (context.Items.TryGetValue("CurrentUser", out var userObj) && userObj is CurrentUserContext user)
        {
            return user;
        }

        // 2. Otherwise, parse from headers/cookies manually (works in dev or when bypass is active)
        var token = string.Empty;
        var authHeader = context.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrWhiteSpace(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            token = authHeader["Bearer ".Length..].Trim();
        }
        else if (context.Request.Cookies.TryGetValue("sso_token", out var cookieToken))
        {
            token = cookieToken;
        }

        if (string.IsNullOrWhiteSpace(token)) return null;

        try
        {
            var parts = token.Split('.');
            if (parts.Length < 2) return null;
            var payloadPart = parts[1];
            payloadPart = payloadPart.Replace('-', '+').Replace('_', '/');
            switch (payloadPart.Length % 4)
            {
                case 2: payloadPart += "=="; break;
                case 3: payloadPart += "="; break;
            }
            var bytes = Convert.FromBase64String(payloadPart);
            var json = System.Text.Encoding.UTF8.GetString(bytes);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var idStr = root.TryGetProperty("sub", out var idProp) ? idProp.GetString() : null;
            var username = root.TryGetProperty("unique_name", out var userProp) ? userProp.GetString() : string.Empty;
            
            var apps = Array.Empty<string>();
            if (root.TryGetProperty("apps", out var appsProp))
            {
                var appsJson = appsProp.GetString();
                if (!string.IsNullOrEmpty(appsJson))
                {
                    apps = JsonSerializer.Deserialize<string[]>(appsJson) ?? Array.Empty<string>();
                }
            }

            int? locationId = null;
            if (root.TryGetProperty("location_id", out var locProp))
            {
                if (int.TryParse(locProp.GetString(), out var lid))
                {
                    locationId = lid;
                }
            }

            var subRole = root.TryGetProperty("sub_role", out var roleProp) ? roleProp.GetString() : null;

            var contextUser = new CurrentUserContext
            {
                Id = idStr != null ? Guid.Parse(idStr) : Guid.Empty,
                Username = username ?? string.Empty,
                Apps = apps,
                LocationId = locationId,
                SubRole = subRole
            };

            context.Items["CurrentUser"] = contextUser;
            return contextUser;
        }
        catch
        {
            return null;
        }
    }
}

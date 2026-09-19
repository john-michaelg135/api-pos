using Microsoft.EntityFrameworkCore;
using Infrastructures.Persistence;
using Infrastructures.Externals;
using Applications.Interfaces;
using Applications.Services;
using Api.Middlewares;


DotNetEnv.Env.Load();

// ── Load .env file (if present) so `dotnet run` works without scripts ──
var envPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".env");
if (File.Exists(envPath))
{
    foreach (var line in File.ReadAllLines(envPath))
    {
        var trimmed = line.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#')) continue;
        var idx = trimmed.IndexOf('=');
        if (idx <= 0) continue;
        var key   = trimmed[..idx].Trim();
        var value = trimmed[(idx + 1)..].Split('#')[0].Trim(); // strip inline comments
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
            Environment.SetEnvironmentVariable(key, value);
    }
}


var builder = WebApplication.CreateBuilder(args);

// ── Kestrel limits ──
// The web-pos frontend proxies /api-pos/* through Next.js, which forwards the
// browser's NextAuth session cookie. For cashier accounts that cookie is large
// (multi-chunk, ~55 KB) and exceeds Kestrel's default request header limit,
// causing HTTP 431 (Request Header Fields Too Large). api-pos does not use that
// cookie (it authenticates via Bearer tokens), but we raise the limit so the
// oversized header is tolerated rather than rejected.
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestHeadersTotalSize = 128 * 1024; // 128 KB (default 32 KB)
});

// ── Controllers & OpenAPI ──
builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSignalR(); // US-POS-025

// ── Dependency Injection — POS Services ──

// Sprint 1
builder.Services.AddScoped<IProductCatalogService, ProductCatalogService>();
builder.Services.AddScoped<IOrderEntryService, OrderEntryService>();
builder.Services.AddScoped<ILocationService, LocationService>();

// Sprint 2
builder.Services.AddScoped<IOrderManagementService, OrderManagementService>();
builder.Services.AddScoped<IInventoryService, InventoryService>();
builder.Services.AddScoped<IAnalyticsService, AnalyticsService>();

// Sprint 3
builder.Services.AddScoped<IRefundService, RefundService>();
builder.Services.AddScoped<IStockAdjustmentService, StockAdjustmentService>();
builder.Services.AddScoped<IScmsIntegrationService, ScmsIntegrationService>();
builder.Services.AddScoped<IReconciliationService, ReconciliationService>(); // POS↔SCM reconciliation
builder.Services.AddHostedService<ReconciliationSweepBackgroundService>(); // auto-retry failed SCM status callbacks
builder.Services.AddScoped<ICrmsQueryService, CrmsQueryService>();

builder.Services.AddScoped<IXenditService, XenditService>();

builder.Services.AddScoped<IRefundNotificationService, RefundNotificationService>(); // US-POS-025
builder.Services.AddScoped<IAuditLogService, AuditLogService>(); // US-POS-027
builder.Services.AddScoped<AuditLogClient>(); // US-POS-027

// E-Commerce Module 4
builder.Services.AddScoped<ICustomerPortalService, CustomerPortalService>();


// ── HTTP Clients ──

// Auth service client (US-POS-023)
var authServiceUrl = Environment.GetEnvironmentVariable("AUTH_SERVICE_URL") ?? "http://api-auth:5000";
builder.Services.AddHttpClient("AuthService", client =>
{
    client.BaseAddress = new Uri(authServiceUrl);
    client.Timeout     = TimeSpan.FromSeconds(5);
});

// SCMS integration client (US-POS-028)
var scmsApiUrl = Environment.GetEnvironmentVariable("SCMS_API_BASE_URL") ?? "http://api-scm:5000";
builder.Services.AddHttpClient<ScmsApiClient>(client =>
{
    client.BaseAddress = new Uri(scmsApiUrl);
    client.Timeout     = TimeSpan.FromSeconds(30);
});

// Xendit client (US-POS-Xendit)
builder.Services.AddHttpClient("XenditClient", client =>
{
    client.BaseAddress = new Uri("https://api.xendit.co/");
    client.Timeout     = TimeSpan.FromSeconds(15);
    var secretKey = Environment.GetEnvironmentVariable("XENDIT_SECRET_KEY") ?? string.Empty;
    var base64Key = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"{secretKey}:"));
    client.DefaultRequestHeaders.Authorization =
        new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", base64Key);
});

// Shared Audit service client (US-POS-027)
var auditServiceUrl = Environment.GetEnvironmentVariable("AUDIT_SERVICE_URL") ?? "http://api-audit-logs:5000/";
builder.Services.AddHttpClient("AuditService", client =>
{
    client.BaseAddress = new Uri(auditServiceUrl);
    client.Timeout     = TimeSpan.FromSeconds(5);
});

// ── Database ──
// Supports a single DATABASE_URL (Neon / Render managed Postgres, SSL enforced)
// or the split POSTGRES_* vars for local docker. See ConnectionStringFactory.
var connectionString = ConnectionStringFactory.Build();

builder.Services.AddDbContext<PosDbContext>(options =>
    options.UseNpgsql(connectionString));

// ── CORS for the web-pos frontend ──
// Production: restrict to the deployed web-pos origin (WEB_POS_URL). Multiple
// origins may be provided comma-separated. Local dev origins are always allowed.
var corsOrigins = new List<string>
{
    "http://localhost:3002",
    "https://localhost:3002",
};
var webPosUrl = Environment.GetEnvironmentVariable("WEB_POS_URL");
if (!string.IsNullOrWhiteSpace(webPosUrl))
{
    corsOrigins.AddRange(
        webPosUrl.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                 .Select(o => o.TrimEnd('/')));
}

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
        policy.WithOrigins(corsOrigins.Distinct().ToArray())
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials());
});

// ── Authentication (br-auth integration guide, Step 13) ──
// Validate the bearer access token locally against the Auth Service's published
// OIDC signing keys (via {JWT_AUTHORITY}/.well-known/openid-configuration).
// This replaces the per-request call to /api-auth/validate-token.
// Enforcement is gated by AUTH_MIDDLEWARE_ENABLED so local dev can bypass auth.
var authEnabled = string.Equals(
    Environment.GetEnvironmentVariable("AUTH_MIDDLEWARE_ENABLED"), "true",
    StringComparison.OrdinalIgnoreCase);

// JWT_AUTHORITY is the public HTTPS root of the Auth Service (guide naming).
// Fall back to AUTH_SERVICE_URL (existing var) then localhost for dev.
var jwtAuthority =
    Environment.GetEnvironmentVariable("JWT_AUTHORITY")
    ?? Environment.GetEnvironmentVariable("AUTH_SERVICE_URL")
    ?? "https://localhost:5001";

if (authEnabled)
{
    builder.Services
        .AddAuthentication(Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.Authority = jwtAuthority;
            options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
            // The current Auth Service does not issue resource audiences, so
            // audience validation stays disabled (per the guide).
            options.TokenValidationParameters.ValidateAudience = false;
        });

    builder.Services.AddAuthorization();
}

var app = builder.Build();

// ── Auto-run migrations & seed data ──
// Set RUN_DB_MIGRATIONS=true on first deploy (and whenever new migrations ship)
// so the managed database schema is created/updated at startup. Seeding only
// runs once the schema exists, so a fresh DB with migrations disabled won't crash.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PosDbContext>();
    var startupLogger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
        .CreateLogger("Startup.Database");

    var runMigrations = string.Equals(
        Environment.GetEnvironmentVariable("RUN_DB_MIGRATIONS"), "true",
        StringComparison.OrdinalIgnoreCase);

    try
    {
        if (runMigrations)
        {
            startupLogger.LogInformation("RUN_DB_MIGRATIONS=true — applying EF Core migrations...");
            await db.Database.MigrateAsync();
        }

        // Only seed when the schema is present. On a fresh database with
        // migrations disabled, skip seeding rather than crash on missing tables.
        var pendingMigrations = await db.Database.GetPendingMigrationsAsync();
        var appliedMigrations = await db.Database.GetAppliedMigrationsAsync();
        if (appliedMigrations.Any() && !pendingMigrations.Any())
        {
            await DbSeeder.SeedAsync(db);
        }
        else if (!appliedMigrations.Any())
        {
            startupLogger.LogWarning(
                "Database has no applied migrations. Skipping seed. " +
                "Set RUN_DB_MIGRATIONS=true to create the schema on startup.");
        }
        else
        {
            startupLogger.LogWarning(
                "Database has pending migrations ({Count}). Skipping seed until migrated.",
                pendingMigrations.Count());
        }
    }
    catch (Exception ex)
    {
        startupLogger.LogError(ex, "Database startup (migrate/seed) failed.");
        throw; // fail fast — a broken DB connection should not serve traffic
    }
}

// ── HTTP Pipeline ──
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwaggerUi(options =>
    {
        options.DocumentPath = "/openapi/v1.json";
    });
}

// app.UseHttpsRedirection();

app.UseCors("AllowFrontend");

// US-POS-023: Authentication + RBAC (br-auth guide Step 13).
// JwtBearer validates the token signature against the Auth Service's OIDC keys.
// The bridge middleware then requires an authenticated user (public routes excepted)
// and projects the validated claims into CurrentUserContext so existing services
// (GetCurrentUser()) keep working unchanged. All gated by AUTH_MIDDLEWARE_ENABLED.
if (authEnabled)
{
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseMiddleware<CurrentUserBridgeMiddleware>();
}

// Rate limiting — placed after auth so the authenticated user identity is available.
// Blocks duplicate submissions (double-clicked checkout, spammed refund/approve, etc.)
// on endpoints marked with [RateLimit]. Toggle with RATE_LIMIT_ENABLED=true/false.
app.UseMiddleware<RateLimitingMiddleware>();

// ── Health check ──
// Lightweight liveness endpoint for the platform health check (Render's
// healthCheckPath: /health). Bypasses auth via the public-route list.
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.MapControllers();
app.MapHub<Api.Hubs.RefundHub>("/hubs/refund");

app.Run();

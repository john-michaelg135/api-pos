using Microsoft.EntityFrameworkCore;
using Infrastructures.Persistence;
using Infrastructures.Externals;
using Applications.Interfaces;
using Applications.Services;
using Api.Middlewares;

var builder = WebApplication.CreateBuilder(args);

// ── Controllers & OpenAPI ──
builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddHttpContextAccessor();

// ── Dependency Injection — POS Services ──

// Sprint 1
builder.Services.AddScoped<IProductCatalogService, ProductCatalogService>();
builder.Services.AddScoped<IOrderEntryService, OrderEntryService>();
builder.Services.AddScoped<ILocationService, LocationService>();

// Sprint 2
builder.Services.AddScoped<IOrderManagementService, OrderManagementService>();
builder.Services.AddScoped<IInventoryService, InventoryService>();
builder.Services.AddScoped<IAnalyticsService, AnalyticsService>();
builder.Services.AddScoped<IVoucherService, VoucherService>();

// Sprint 3
builder.Services.AddScoped<IRefundService, RefundService>();
builder.Services.AddScoped<IStockAdjustmentService, StockAdjustmentService>();
builder.Services.AddScoped<IScmsIntegrationService, ScmsIntegrationService>();
builder.Services.AddScoped<ICrmsQueryService, CrmsQueryService>();

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

// ── Database ──
var connectionString =
    $"Host={Environment.GetEnvironmentVariable("POSTGRES_DB_HOST")};" +
    $"Port={Environment.GetEnvironmentVariable("POSTGRES_DB_PORT")};" +
    $"Database=pos_db;" +
    $"Username={Environment.GetEnvironmentVariable("POSTGRES_USERNAME")};" +
    $"Password={Environment.GetEnvironmentVariable("POSTGRES_PASSWORD")}";

builder.Services.AddDbContext<PosDbContext>(options =>
    options.UseNpgsql(connectionString));

// ── CORS for frontend (Next.js on port 3000) ──
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
        policy.WithOrigins("http://localhost:3000")
              .AllowAnyHeader()
              .AllowAnyMethod());
});

var app = builder.Build();

// ── Auto-run migrations & seed data ──
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PosDbContext>();
    if (string.Equals(Environment.GetEnvironmentVariable("RUN_DB_MIGRATIONS"), "true", StringComparison.OrdinalIgnoreCase))
    {
        db.Database.Migrate();
    }
    await DbSeeder.SeedAsync(db);
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

// US-POS-023: Auth validation middleware — validates JWT on every API call
// Toggle with AUTH_MIDDLEWARE_ENABLED=true/false
app.UseMiddleware<AuthValidationMiddleware>();

app.UseAuthorization();

app.MapControllers();

app.Run();

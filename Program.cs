using Microsoft.EntityFrameworkCore;
using Infrastructures.Persistence;
using Applications.Interfaces;
using Applications.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddOpenApi();

// Dependency injection for POS services
builder.Services.AddScoped<IProductCatalogService, ProductCatalogService>();
builder.Services.AddScoped<IOrderEntryService, OrderEntryService>();
builder.Services.AddScoped<ILocationService, LocationService>();
// Sprint 2
builder.Services.AddScoped<IOrderManagementService, OrderManagementService>();
builder.Services.AddScoped<IInventoryService, InventoryService>();
builder.Services.AddScoped<IAnalyticsService, AnalyticsService>();

// Database connection string from environment variables
var connectionString =
    $"Host={Environment.GetEnvironmentVariable("POSTGRES_DB_HOST")};"+
    $"Port={Environment.GetEnvironmentVariable("POSTGRES_DB_PORT")};"+
    $"Database=pos_db;" +
    $"Username={Environment.GetEnvironmentVariable("POSTGRES_USERNAME")};"+
    $"Password={Environment.GetEnvironmentVariable("POSTGRES_PASSWORD")}";

builder.Services.AddDbContext<PosDbContext>(options =>
    options.UseNpgsql(connectionString));

// CORS for frontend (Next.js on port 3000)
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
        policy.WithOrigins("http://localhost:3000")
              .AllowAnyHeader()
              .AllowAnyMethod());
});

var app = builder.Build();

// Auto-run migrations if RUN_DB_MIGRATIONS=true
if (string.Equals(Environment.GetEnvironmentVariable("RUN_DB_MIGRATIONS"), "true", StringComparison.OrdinalIgnoreCase))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<PosDbContext>();
    db.Database.Migrate();
}

// Configure the HTTP request pipeline.
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

app.UseAuthorization();

app.MapControllers();

app.Run();

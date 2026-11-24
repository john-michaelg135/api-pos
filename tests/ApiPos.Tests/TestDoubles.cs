using System.Net;
using Api.Contracts.Inventory;
using Api.Contracts.Shared;
using Api.Middlewares;
using Applications.Audit;
using Applications.Interfaces;
using Applications.Services;
using Infrastructures.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ApiPos.Tests;

/// <summary>Captures every body the boundary hands over; optionally throws instead.</summary>
public sealed class FakeAuditWriter : IAuditWriter
{
    public List<AuditLogEntry> Entries { get; } = [];
    public Exception? Throw { get; set; }

    public Task<AuditWriteResult> WriteAsync(AuditLogEntry entry, CancellationToken cancellationToken = default)
    {
        if (Throw is not null) throw Throw;
        Entries.Add(entry);
        return Task.FromResult(AuditWriteResult.FromStatus(201, Guid.NewGuid().ToString()));
    }
}

public sealed class ListLogger<T> : ILogger<T>
{
    public List<string> Messages { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Messages.Add(formatter(state, exception));
}

public sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;
    public HttpRequestMessage? Request { get; private set; }
    public string? Body { get; private set; }

    public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;

    public static StubHandler Status(HttpStatusCode status, string body = "") =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) }));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Request = request;
        Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        return await _respond(request, cancellationToken);
    }
}

public sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

/// <summary>Staff numbers keyed by Portal user, like PosStaffIdentity but without Postgres.</summary>
public sealed class FakeStaffIdentity(IHttpContextAccessor accessor) : IPosStaffIdentity
{
    private readonly Dictionary<string, int> _numbers = new();

    public Task<int?> CurrentStaffIdAsync(CancellationToken cancellationToken = default)
    {
        var id = (accessor.HttpContext?.Items["CurrentUser"] as CurrentUserContext)?.PortalUserId;
        if (id is null) return Task.FromResult<int?>(null);
        if (!_numbers.TryGetValue(id, out var number)) _numbers[id] = number = _numbers.Count + 1;
        return Task.FromResult<int?>(number);
    }

    public Task<Dictionary<int, string>> NamesAsync(IEnumerable<int> staffIds, CancellationToken cancellationToken = default) =>
        Task.FromResult(new Dictionary<int, string>());
}

public sealed class NoopInventory : IInventoryService
{
    public Task<StockResponseDto> ReceiveStockAsync(StockReceivingDto dto) => throw new NotSupportedException();
    public Task<List<StockReceivingResponseDto>> GetStockReceivingHistoryAsync() => throw new NotSupportedException();
    public Task<List<StockResponseDto>> GetStockByLocationAsync(int locationId) => throw new NotSupportedException();
    public Task<List<StockResponseDto>> GetAllStockAsync() => throw new NotSupportedException();
    public Task<List<StockMovementResponseDto>> GetStockMovementsAsync(int? locationId, DateTime? from, DateTime? to) => throw new NotSupportedException();
    public Task<List<LowStockAlertDto>> GetLowStockAlertsAsync() => throw new NotSupportedException();
    public Task DeductStockAsync(int variationId, int locationId, int quantity, int? orderId = null, string movementType = "Sale", string? reference = null, string? notes = null) => Task.CompletedTask;
    public Task RestoreStockAsync(int variationId, int locationId, int quantity, int? orderId = null, string movementType = "Refund", string? reference = null) => Task.CompletedTask;
    public Task RecordStockLossAsync(int variationId, int locationId, int quantity, int? orderId = null, string? reference = null, string? notes = null) => Task.CompletedTask;
    public Task<StockAdjustmentResponseDto> SubmitStockAdjustmentAsync(StockAdjustmentRequestDto dto) => throw new NotSupportedException();
    public Task<StockAdjustmentResponseDto> ApproveStockAdjustmentAsync(int adjustmentId, int approvedBy) => throw new NotSupportedException();
}

/// <summary>An in-memory POS with a signed-in Portal user that tests can switch.</summary>
public sealed class PosHarness : IDisposable
{
    public PosDbContext Db { get; }
    public HttpContextAccessor Accessor { get; } = new();
    public FakeAuditWriter Writer { get; } = new();
    public ListLogger<AuditLogService> AuditLogger { get; } = new();
    public AuditLogService Audit { get; }

    public PosHarness(bool enabled = true)
    {
        Db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        Audit = new AuditLogService(Writer, Accessor,
            new AuditDeliveryOptions { Requested = enabled, GatewayUrl = "https://gateway.test", CallerKey = "caller-key" },
            AuditLogger);
        SignIn(null);
    }

    public void SignIn(CurrentUserContext? user)
    {
        // GetCurrentUser() reloads branch assignments through RequestServices, as in a real request.
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, Db);
        var http = new DefaultHttpContext
        {
            TraceIdentifier = $"req-{Guid.NewGuid():N}",
            RequestServices = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services),
        };
        if (user is not null) http.Items["CurrentUser"] = user;
        Accessor.HttpContext = http;
    }

    public static CurrentUserContext Staff(string portalId, string name, string role, params int[] locations) => new()
    {
        PortalUserId = portalId,
        Username = portalId,
        DisplayName = name,
        Role = role,
        SubRole = role == "Cashier" ? "Cashier" : "Manager",
        LocationIds = locations,
        LocationId = locations.FirstOrDefault(),
        Apps = ["POS"],
    };

    public void Dispose() => Db.Dispose();
}

public static class NullLogger
{
    public static ILogger<T> For<T>() => NullLogger<T>.Instance;
}

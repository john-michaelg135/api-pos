using Api.Contracts.OrderManagement;
using Api.Middlewares;
using Applications.Interfaces;
using Applications.ProofFiles;
using Applications.Services;
using Domains.Entities;
using Microsoft.EntityFrameworkCore;

namespace ApiPos.Tests;

public sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>File storage in memory: what the gateway would hold, plus switches for each failure.</summary>
public sealed class FakeFileStorage : IFileStorage
{
    public sealed class Entry
    {
        public required UploadRequest Declared { get; init; }
        public byte[]? Bytes { get; set; }
        public string Status { get; set; } = "pending";
        public string Retention { get; set; } = FileStorageValues.Temporary;
        public DateTimeOffset? ExpiresAt { get; set; }
        public HashSet<FileLink> Links { get; } = [];
    }

    private readonly ManualClock _clock;
    public FakeFileStorage(ManualClock clock) => _clock = clock;

    public Dictionary<Guid, Entry> Files { get; } = new();
    public List<Guid> Deleted { get; } = [];
    public List<string> Calls { get; } = [];
    public bool Down { get; set; }
    public bool DeleteFails { get; set; }
    public int? ConfirmStatus { get; set; }
    public string? ConfirmContentTypeOverride { get; set; }

    /// <summary>The browser's PUT to the signed address.</summary>
    public void Put(Guid id, byte[] bytes) => Files[id].Bytes = bytes;

    private void Gate(string call)
    {
        Calls.Add(call);
        if (Down) throw new FileStorageException(null, "gateway down");
    }

    private Entry Find(Guid id) =>
        Files.TryGetValue(id, out var e) && !Deleted.Contains(id) ? e : throw new FileStorageException(404, "File not found.");

    private StoredFile Describe(Guid id, Entry e) => new()
    {
        Id = id, Status = e.Status, Filename = e.Declared.Filename, ContentType = ConfirmContentTypeOverride ?? e.Declared.ContentType,
        Size = e.Declared.Size, Visibility = e.Declared.Visibility, RetentionType = e.Retention, ExpiresAt = e.ExpiresAt,
    };

    public Task<UploadAddress> RequestUploadAsync(UploadRequest request, CancellationToken cancellationToken = default)
    {
        Gate("request");
        var id = Guid.NewGuid();
        Files[id] = new Entry { Declared = request };
        return Task.FromResult(new UploadAddress
        {
            Id = id, Status = "pending", Filename = request.Filename, ContentType = request.ContentType, Size = request.Size,
            Visibility = request.Visibility, RetentionType = request.RetentionType,
            UploadUrl = $"https://bucket.test/upload/{id}?sig=secret", UploadUrlExpiresInSeconds = 900,
        });
    }

    public Task<StoredFile> ConfirmAsync(Guid fileId, CancellationToken cancellationToken = default)
    {
        Gate("confirm");
        var e = Find(fileId);
        if (ConfirmStatus is { } status) throw new FileStorageException(status, $"File storage answered {status}.");
        if (e.Status == "active" || e.Bytes is null) throw new FileStorageException(409, "Nothing uploaded.");
        e.Status = "active";
        e.ExpiresAt = _clock.GetUtcNow().AddDays(7);
        return Task.FromResult(Describe(fileId, e));
    }

    public Task<ViewedFile> ViewAsync(Guid fileId, CancellationToken cancellationToken = default)
    {
        Gate("view");
        var e = Find(fileId);
        if (e.Status != "active") throw new FileStorageException(404, "File not found.");
        var d = Describe(fileId, e);
        return Task.FromResult(new ViewedFile
        {
            Id = d.Id, Status = d.Status, Filename = d.Filename, ContentType = d.ContentType, Size = d.Size,
            Visibility = d.Visibility, RetentionType = d.RetentionType, ExpiresAt = d.ExpiresAt,
            Url = $"https://bucket.test/view/{fileId}?sig=fresh",
        });
    }

    public Task<StoredFile> SetRetentionAsync(Guid fileId, string retentionType, CancellationToken cancellationToken = default)
    {
        Gate("retention");
        var e = Find(fileId);
        e.Retention = retentionType;
        e.ExpiresAt = retentionType == FileStorageValues.Permanent ? null : _clock.GetUtcNow().AddDays(7);
        return Task.FromResult(Describe(fileId, e));
    }

    public Task AddLinkAsync(Guid fileId, FileLink link, CancellationToken cancellationToken = default)
    {
        Gate("link");
        Find(fileId).Links.Add(link);
        return Task.CompletedTask;
    }

    public Task RemoveLinkAsync(Guid fileId, FileLink link, CancellationToken cancellationToken = default)
    {
        Gate("unlink");
        Find(fileId).Links.Remove(link);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid fileId, CancellationToken cancellationToken = default)
    {
        Gate("delete");
        if (DeleteFails) throw new FileStorageException(503, "unavailable");
        Deleted.Add(fileId);
        return Task.CompletedTask;
    }

    public Task<byte[]> ReadOriginalAsync(Uri signedUrl, long maxBytes, CancellationToken cancellationToken = default)
    {
        Gate("read");
        var id = Guid.Parse(signedUrl.Segments[^1]);
        var bytes = Find(id).Bytes ?? [];
        return Task.FromResult(bytes.Take((int)maxBytes + 1).ToArray());
    }
}

public class ProofFileStorageTests
{
    private const string Secret = "test-signing-secret-that-is-long-enough-123456";
    private static readonly CurrentUserContext Cashier = PosHarness.Staff("portal-cashier-7", "Ana Cruz", "Cashier", 8);
    private static readonly CurrentUserContext Manager = PosHarness.Staff("portal-user-42", "Mira Reyes", "Manager", 8);
    private static readonly CurrentUserContext OtherBranchManager = PosHarness.Staff("portal-user-77", "Leo Tan", "Manager", 9);

    private static byte[] Jpeg(int size)
    {
        var bytes = new byte[size];
        bytes[0] = 0xFF; bytes[1] = 0xD8; bytes[2] = 0xFF; bytes[3] = 0xE0;
        bytes[^2] = 0xFF; bytes[^1] = 0xD9;
        return bytes;
    }

    private sealed class World : IDisposable
    {
        public PosHarness Pos { get; } = new();
        public ManualClock Clock { get; } = new(DateTimeOffset.UtcNow);
        public FakeFileStorage Storage { get; }
        public ProofUploadService Uploads { get; }
        public ProofFileFinalizer Finalizer { get; }
        public OrderManagementService Orders { get; }

        public World(string orderStatus = "Completed")
        {
            Storage = new FakeFileStorage(Clock);
            var options = new ProofStorageOptions { GatewayUrl = "https://gateway.test", CallerKey = "caller-key", SigningSecret = Secret };
            Uploads = new ProofUploadService(Pos.Db, Storage, options, Pos.Accessor, NullLogger.For<ProofUploadService>(), Clock);
            Finalizer = new ProofFileFinalizer(Pos.Db, Storage, NullLogger.For<ProofFileFinalizer>(), Clock);
            Orders = new OrderManagementService(Pos.Db, new NoopInventory(), Pos.Accessor, Pos.Audit, new FakeStaffIdentity(Pos.Accessor),
                Uploads, Finalizer, Storage);

            Pos.Db.Locations.Add(new Location { LocationId = 8, LocationName = "Makati Branch", CreatedAt = DateTime.UtcNow });
            Pos.Db.Locations.Add(new Location { LocationId = 9, LocationName = "Pasig Branch", CreatedAt = DateTime.UtcNow });
            Pos.Db.Products.Add(new Product { ProductId = 1, ProductName = "Ube Halaya", ProductCategory = "Spreads" });
            Pos.Db.ProductVariations.Add(new ProductVariation { VariationId = 11, ProductId = 1, VariationName = "UH-300|Jar|300g" });
            Pos.Db.Orders.Add(new Order
            {
                OrderId = 4821, OrderNumber = "ORD-20261120-0001", OrderType = "Institutional", OrderSource = "POS",
                LocationId = 8, PaymentMethod = "GCash", PaymentStatus = "Paid", OrderStatus = orderStatus,
                TotalAmount = 450m, CreatedAt = DateTime.UtcNow.AddDays(-1), UpdatedAt = DateTime.UtcNow,
            });
            Pos.Db.OrderItems.Add(new OrderItem { OrderId = 4821, VariationId = 11, Quantity = 3, UnitPrice = 150m, Subtotal = 450m });
            Pos.Db.PosUserPins.Add(new PosUserPin { AuthUserId = Manager.PortalUserId!, PinCode = "2468" });
            foreach (var who in new[] { Cashier, Manager })
                Pos.Db.PosUserLocations.Add(new PosUserLocation { AuthUserId = who.PortalUserId!, LocationId = 8, IsPrimary = true });
            Pos.Db.PosUserLocations.Add(new PosUserLocation { AuthUserId = OtherBranchManager.PortalUserId!, LocationId = 9, IsPrimary = true });
            Pos.Db.SaveChanges();
        }

        /// <summary>Steps 1–3 as the picker runs them.</summary>
        public async Task<(string Reference, Guid FileId)> UploadAsync(string kind, byte[]? bytes = null, int size = 184_320)
        {
            bytes ??= Jpeg(size);
            var started = await Uploads.StartAsync(4821, kind, new ProofUploadRequestDto { Filename = $"{kind}-proof.jpg", ContentType = "image/jpeg", Size = size });
            var id = Guid.Parse(new Uri(started.UploadUrl).Segments[^1]);
            Storage.Put(id, bytes);
            var ready = await Uploads.ConfirmAsync(4821, kind, started.PendingReference);
            return (ready.ProofReference, id);
        }

        public void Dispose() => Pos.Dispose();
    }

    // ── References ──

    [Fact]
    public void References_open_only_untampered_and_with_the_same_secret()
    {
        var codec = new ProofReferenceCodec(Secret);
        var reference = new ProofReference
        {
            FileId = Guid.NewGuid(), StaffId = "portal-cashier-7", OrderId = 4821, Kind = ProofKinds.Refund, Filename = "a.jpg",
            ContentType = "image/jpeg", Size = 10, Phase = ProofReferencePhases.Pending, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds(),
        };
        var sealedRef = codec.Seal(reference);

        Assert.Equal(reference, codec.Open(sealedRef));
        var parts = sealedRef.Split('.');
        var forged = new ProofReferenceCodec(Secret).Seal(reference with { OrderId = 1 }).Split('.')[1];
        Assert.Null(codec.Open($"{parts[0]}.{forged}.{parts[2]}"));
        Assert.Null(new ProofReferenceCodec(Secret + "-other").Open(sealedRef));
        Assert.Null(codec.Open("garbage"));
        Assert.Throws<ArgumentException>(() => new ProofReferenceCodec("too-short"));
    }

    [Fact]
    public async Task Gateway_client_sends_json_metadata_with_the_caller_key_and_maps_refusals()
    {
        var id = Guid.NewGuid();
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.Created)
        {
            Content = new StringContent($$"""{"id":"{{id}}","status":"pending","filename":"refund-proof.jpg","contentType":"image/jpeg","size":184320,"visibility":"private","retentionType":"temporary","expiresAt":null,"variants":[],"uploadUrl":"https://bucket.test/u","uploadUrlExpiresInSeconds":900}""",
                System.Text.Encoding.UTF8, "application/json"),
        }));
        var options = new ProofStorageOptions { GatewayUrl = "https://gateway.test", CallerKey = "caller-key", SigningSecret = Secret };
        var client = new Infrastructures.Externals.FileStorageClient(new StubHttpClientFactory(handler), options);

        var pending = await client.RequestUploadAsync(new UploadRequest("refund-proof.jpg", "image/jpeg", 184320, "private", "temporary"));

        Assert.Equal(id, pending.Id);
        Assert.Equal(900, pending.UploadUrlExpiresInSeconds);
        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("https://gateway.test/files", handler.Request.RequestUri!.ToString());
        Assert.Equal("Bearer caller-key", handler.Request.Headers.Authorization!.ToString());
        Assert.Equal("""{"filename":"refund-proof.jpg","contentType":"image/jpeg","size":184320,"visibility":"private","retentionType":"temporary"}""", handler.Body);

        var linkHandler = StubHandler.Status(System.Net.HttpStatusCode.NoContent);
        await new Infrastructures.Externals.FileStorageClient(new StubHttpClientFactory(linkHandler), options)
            .AddLinkAsync(id, ProofKinds.OrderLink(4821, ProofKinds.Refund));
        Assert.Equal($"https://gateway.test/files/{id}/links", linkHandler.Request!.RequestUri!.ToString());
        Assert.Equal("""{"system":"POS","entityType":"order","entityId":"4821","role":"refund-proof"}""", linkHandler.Body);

        var refused = new Infrastructures.Externals.FileStorageClient(new StubHttpClientFactory(StubHandler.Status(System.Net.HttpStatusCode.UnprocessableEntity)), options);
        Assert.Equal(422, (await Assert.ThrowsAsync<FileStorageException>(() => refused.ConfirmAsync(id))).Status);
    }

    // ── Upload and validation ──

    [Fact]
    public async Task Upload_requests_a_private_temporary_jpeg_and_hands_back_only_the_signed_address()
    {
        using var w = new World();
        w.Pos.SignIn(Cashier);

        var started = await w.Uploads.StartAsync(4821, ProofKinds.Refund, new ProofUploadRequestDto { Filename = "../../evil.jpg", ContentType = "image/jpeg", Size = 1234 });

        var file = Assert.Single(w.Storage.Files.Values);
        Assert.Equal(new UploadRequest("evil.jpg", "image/jpeg", 1234, "private", "temporary"), file.Declared);
        Assert.StartsWith("https://bucket.test/upload/", started.UploadUrl);
        Assert.Equal(900, started.UploadUrlExpiresInSeconds);
        Assert.StartsWith("v1.", started.PendingReference);
    }

    [Fact]
    public async Task Size_and_type_limits_match_the_old_data_url_budget()
    {
        using var w = new World();
        w.Pos.SignIn(Cashier);

        await w.Uploads.StartAsync(4821, ProofKinds.Refund, new ProofUploadRequestDto { ContentType = "image/jpeg", Size = ProofStorageOptions.MaxJpegBytes });
        var tooBig = await Assert.ThrowsAsync<ProofUploadException>(() =>
            w.Uploads.StartAsync(4821, ProofKinds.Refund, new ProofUploadRequestDto { ContentType = "image/jpeg", Size = ProofStorageOptions.MaxJpegBytes + 1 }));
        var png = await Assert.ThrowsAsync<ProofUploadException>(() =>
            w.Uploads.StartAsync(4821, ProofKinds.Refund, new ProofUploadRequestDto { ContentType = "image/png", Size = 100 }));
        var kind = await Assert.ThrowsAsync<ProofUploadException>(() =>
            w.Uploads.StartAsync(4821, "avatar", new ProofUploadRequestDto { ContentType = "image/jpeg", Size = 100 }));

        Assert.Equal(400, tooBig.Status);
        Assert.Equal(400, png.Status);
        Assert.Equal(404, kind.Status);
        Assert.Single(w.Storage.Files); // only the accepted one reached storage
    }

    [Fact]
    public async Task Other_branches_and_roles_without_the_action_are_refused_at_every_step()
    {
        using var w = new World(orderStatus: "Shipped");

        w.Pos.SignIn(OtherBranchManager);
        var branch = await Assert.ThrowsAsync<ProofUploadException>(() =>
            w.Uploads.StartAsync(4821, ProofKinds.Refund, new ProofUploadRequestDto { ContentType = "image/jpeg", Size = 100 }));
        w.Pos.SignIn(Cashier);
        var role = await Assert.ThrowsAsync<ProofUploadException>(() =>
            w.Uploads.StartAsync(4821, ProofKinds.Delivery, new ProofUploadRequestDto { ContentType = "image/jpeg", Size = 100 }));
        w.Pos.SignIn(null);
        var anonymous = await Assert.ThrowsAsync<ProofUploadException>(() =>
            w.Uploads.StartAsync(4821, ProofKinds.Refund, new ProofUploadRequestDto { ContentType = "image/jpeg", Size = 100 }));

        Assert.Equal(403, branch.Status);
        Assert.Equal(403, role.Status);
        Assert.Equal(401, anonymous.Status);
        Assert.Empty(w.Storage.Files);

        // A pending reference can't be confirmed by someone else either.
        w.Pos.SignIn(Manager);
        var started = await w.Uploads.StartAsync(4821, ProofKinds.Refund, new ProofUploadRequestDto { ContentType = "image/jpeg", Size = 100 });
        w.Pos.SignIn(Cashier);
        var stranger = await Assert.ThrowsAsync<ProofUploadException>(() => w.Uploads.ConfirmAsync(4821, ProofKinds.Refund, started.PendingReference));
        Assert.Equal(400, stranger.Status);
    }

    [Fact]
    public async Task Confirm_reports_missing_bytes_and_declaration_mismatches_without_issuing_a_ready_reference()
    {
        using var w = new World();
        w.Pos.SignIn(Cashier);
        var started = await w.Uploads.StartAsync(4821, ProofKinds.Refund, new ProofUploadRequestDto { ContentType = "image/jpeg", Size = 2000 });

        // Browser PUT failed: nothing in storage yet.
        var missing = await Assert.ThrowsAsync<ProofUploadException>(() => w.Uploads.ConfirmAsync(4821, ProofKinds.Refund, started.PendingReference));
        Assert.Equal(409, missing.Status);

        w.Storage.ConfirmStatus = 422;
        var mismatch = await Assert.ThrowsAsync<ProofUploadException>(() => w.Uploads.ConfirmAsync(4821, ProofKinds.Refund, started.PendingReference));
        Assert.Equal(422, mismatch.Status);
    }

    [Fact]
    public async Task Bytes_that_are_not_the_declared_jpeg_are_rejected_and_the_upload_discarded()
    {
        using var w = new World();
        w.Pos.SignIn(Cashier);

        var notJpeg = new byte[2000]; // right size, PNG-ish header
        notJpeg[0] = 0x89; notJpeg[1] = 0x50;
        var wrongBytes = await Assert.ThrowsAsync<ProofUploadException>(() => w.UploadAsync(ProofKinds.Refund, notJpeg, size: 2000));
        Assert.Equal(422, wrongBytes.Status);
        Assert.Single(w.Storage.Deleted);

        var shorter = await Assert.ThrowsAsync<ProofUploadException>(() => w.UploadAsync(ProofKinds.Refund, Jpeg(1500), size: 2000));
        Assert.Equal(422, shorter.Status);
        Assert.Equal(2, w.Storage.Deleted.Count);

        w.Storage.ConfirmContentTypeOverride = "image/png";
        w.Storage.DeleteFails = true;
        var cleanup = await Assert.ThrowsAsync<ProofUploadException>(() => w.UploadAsync(ProofKinds.Refund));
        Assert.True(cleanup.CleanupFailed);
    }

    [Fact]
    public async Task A_ready_reference_is_good_only_for_its_uploader_order_purpose_and_lifetime()
    {
        using var w = new World();
        w.Pos.SignIn(Cashier);
        var (reference, _) = await w.UploadAsync(ProofKinds.Refund);
        var started = await w.Uploads.StartAsync(4821, ProofKinds.Refund, new ProofUploadRequestDto { ContentType = "image/jpeg", Size = 100 });

        Assert.Equal(4821, w.Uploads.OpenReadyReference(4821, ProofKinds.Refund, reference).OrderId);
        Assert.Throws<InvalidOperationException>(() => w.Uploads.OpenReadyReference(4821, ProofKinds.Delivery, reference));
        Assert.Throws<InvalidOperationException>(() => w.Uploads.OpenReadyReference(4822, ProofKinds.Refund, reference));
        Assert.Throws<InvalidOperationException>(() => w.Uploads.OpenReadyReference(4821, ProofKinds.Refund, started.PendingReference));
        w.Pos.SignIn(Manager);
        Assert.Throws<InvalidOperationException>(() => w.Uploads.OpenReadyReference(4821, ProofKinds.Refund, reference));
        w.Pos.SignIn(Cashier);
        w.Clock.Now = w.Clock.Now.AddMinutes(61);
        Assert.Throws<InvalidOperationException>(() => w.Uploads.OpenReadyReference(4821, ProofKinds.Refund, reference));
    }

    // ── Saving with the order action ──

    [Fact]
    public async Task A_refund_photo_is_saved_as_a_file_id_then_linked_and_made_permanent()
    {
        using var w = new World();
        w.Pos.SignIn(Cashier);
        var (reference, fileId) = await w.UploadAsync(ProofKinds.Refund);

        var result = await w.Orders.RequestRefundAsync(4821, "Spoiled", null, null, reference);

        Assert.Equal("Refund Requested", result!.OrderStatus);
        Assert.True(result.HasRefundProof);
        Assert.False(result.RefundProofPending);
        var row = await w.Pos.Db.OrderRefundProofs.SingleAsync();
        Assert.Equal(fileId, row.ProofFileId);
        Assert.Null(row.ProofImage);
        var stored = w.Storage.Files[fileId];
        Assert.Equal(FileStorageValues.Permanent, stored.Retention);
        Assert.Null(stored.ExpiresAt);
        Assert.Equal(new FileLink("POS", "order", "4821", "refund-proof"), Assert.Single(stored.Links));

        // A manager at the branch views it with a fresh signed address; another branch can't.
        w.Pos.SignIn(Manager);
        var view = await w.Orders.GetRefundProofImageAsync(4821);
        Assert.Equal(ProofImageResult.FileSource, view!.Source);
        Assert.StartsWith("https://bucket.test/view/", view.ProofImage);
        w.Pos.SignIn(OtherBranchManager);
        await Assert.ThrowsAsync<InvalidOperationException>(() => w.Orders.GetRefundProofImageAsync(4821));
    }

    [Fact]
    public async Task Required_proof_must_come_with_this_request_in_one_form()
    {
        using var w = new World();
        w.Pos.SignIn(Cashier);
        var (reference, _) = await w.UploadAsync(ProofKinds.Refund);

        await Assert.ThrowsAsync<InvalidOperationException>(() => w.Orders.RequestRefundAsync(4821, "Spoiled", null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => w.Orders.RequestRefundAsync(4821, "Spoiled", null, null, "v1.bogus.ref"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            w.Orders.RequestRefundAsync(4821, "Spoiled", "data:image/jpeg;base64,AAAA", null, reference));
        w.Pos.SignIn(Manager); // a different uploader can't reuse the cashier's photo
        await Assert.ThrowsAsync<InvalidOperationException>(() => w.Orders.RequestRefundAsync(4821, "Spoiled", null, null, reference));

        Assert.Empty(w.Pos.Db.RefundRequests);
        Assert.Empty(w.Pos.Db.ProofFileTasks);
    }

    [Fact]
    public async Task Resubmitting_the_same_reference_creates_no_second_request_or_history()
    {
        using var w = new World();
        w.Pos.SignIn(Cashier);
        var (reference, _) = await w.UploadAsync(ProofKinds.Refund);

        await w.Orders.RequestRefundAsync(4821, "Spoiled", null, null, reference);
        var again = await w.Orders.RequestRefundAsync(4821, "Spoiled", null, null, reference);

        Assert.Equal("Refund Requested", again!.OrderStatus);
        Assert.True(again.HasRefundProof);
        Assert.Single(w.Pos.Db.RefundRequests);
        Assert.Single(w.Pos.Db.OrderStatusHistories);
        Assert.Single(w.Pos.Db.ProofFileTasks);
    }

    [Fact]
    public async Task A_gateway_outage_after_commit_keeps_the_refund_and_recovery_finishes_it_later()
    {
        using var w = new World();
        w.Pos.SignIn(Cashier);
        var (reference, fileId) = await w.UploadAsync(ProofKinds.Refund);
        w.Storage.Down = true;

        var result = await w.Orders.RequestRefundAsync(4821, "Spoiled", null, null, reference);

        Assert.Equal("Refund Requested", result!.OrderStatus);
        Assert.True(result.RefundProofPending);
        var task = await w.Pos.Db.ProofFileTasks.SingleAsync();
        Assert.Equal(ProofFileTaskStages.Pending, task.Stage);
        Assert.Equal("storage_unreachable", task.LastError);

        // "Restart": a fresh finalizer on the persisted state, after the retry delay.
        w.Storage.Down = false;
        w.Clock.Now = w.Clock.Now.AddMinutes(2);
        Assert.Equal(1, await new ProofFileFinalizer(w.Pos.Db, w.Storage, NullLogger.For<ProofFileFinalizer>(), w.Clock).RunDueAsync());

        Assert.Equal(ProofFileTaskStages.Done, (await w.Pos.Db.ProofFileTasks.AsNoTracking().SingleAsync()).Stage);
        Assert.Equal(FileStorageValues.Permanent, w.Storage.Files[fileId].Retention);
        Assert.Single(w.Storage.Files[fileId].Links);
        Assert.Single(w.Pos.Db.RefundRequests);
    }

    [Fact]
    public async Task A_later_refund_photo_replaces_the_rejected_one_and_wins_over_stale_recovery()
    {
        using var w = new World();
        w.Pos.SignIn(Cashier);
        var (firstRef, firstId) = await w.UploadAsync(ProofKinds.Refund);
        w.Storage.Down = true; // first photo stays unattached
        await w.Orders.RequestRefundAsync(4821, "Spoiled", null, null, firstRef);
        w.Storage.Down = false;

        w.Pos.SignIn(Manager);
        await w.Orders.RejectRefundAsync(4821, 0, "Need a clearer photo", "2468");

        w.Pos.SignIn(Cashier);
        var (secondRef, secondId) = await w.UploadAsync(ProofKinds.Refund);
        await w.Orders.RequestRefundAsync(4821, "Spoiled", null, null, secondRef);

        var row = await w.Pos.Db.OrderRefundProofs.AsNoTracking().SingleAsync();
        Assert.Equal(secondId, row.ProofFileId);

        // Stale recovery for the first photo now only retires its (never added) link.
        w.Clock.Now = w.Clock.Now.AddMinutes(5);
        await w.Finalizer.RunDueAsync();
        var tasks = await w.Pos.Db.ProofFileTasks.AsNoTracking().ToDictionaryAsync(t => t.FileId);
        Assert.Equal(ProofFileTaskStages.Retired, tasks[firstId].Stage);
        Assert.Equal(ProofFileTaskStages.Done, tasks[secondId].Stage);
        Assert.Empty(w.Storage.Files[firstId].Links);
        Assert.Equal(FileStorageValues.Temporary, w.Storage.Files[firstId].Retention); // left to expire
        Assert.Single(w.Storage.Files[secondId].Links);
        Assert.DoesNotContain(firstId, w.Storage.Deleted);
    }

    [Fact]
    public async Task Replacing_an_attached_photo_unlinks_the_old_file_only_after_the_new_one_is_attached()
    {
        using var w = new World();
        w.Pos.SignIn(Cashier);
        var (firstRef, firstId) = await w.UploadAsync(ProofKinds.Refund);
        await w.Orders.RequestRefundAsync(4821, "Spoiled", null, null, firstRef);
        w.Pos.SignIn(Manager);
        await w.Orders.RejectRefundAsync(4821, 0, "Need a clearer photo", "2468");

        w.Pos.SignIn(Cashier);
        var (secondRef, secondId) = await w.UploadAsync(ProofKinds.Refund);
        w.Storage.Down = true;
        await w.Orders.RequestRefundAsync(4821, "Spoiled", null, null, secondRef);
        w.Storage.Down = false;

        Assert.Single(w.Storage.Files[firstId].Links); // still linked while the new one is pending

        // The old link's turn may come first; it waits for the new photo, then goes as soon as that is attached.
        w.Clock.Now = w.Clock.Now.AddMinutes(5);
        await w.Finalizer.RunDueAsync();

        Assert.Empty(w.Storage.Files[firstId].Links);
        Assert.Single(w.Storage.Files[secondId].Links);
        Assert.DoesNotContain(firstId, w.Storage.Deleted);
    }

    [Fact]
    public async Task Delivery_confirms_with_a_stored_photo_or_with_none_without_calling_storage()
    {
        using var w = new World(orderStatus: "Shipped");
        w.Pos.SignIn(Manager);
        var (reference, fileId) = await w.UploadAsync(ProofKinds.Delivery);

        var result = await w.Orders.ConfirmInstitutionalDeliveryAsync(4821, new ConfirmInstitutionalDeliveryDto
        {
            ReceivedBy = "Guard on duty", DeliveredAt = DateTime.UtcNow, ProofReference = reference,
        });

        Assert.Equal("Completed", result!.OrderStatus);
        Assert.True(result.DeliveryConfirmation!.HasProofImage);
        Assert.False(result.DeliveryConfirmation.ProofPending);
        Assert.Equal(fileId, (await w.Pos.Db.OrderDeliveryConfirmations.SingleAsync()).ProofFileId);
        Assert.Equal(new FileLink("POS", "order", "4821", "delivery-proof"), Assert.Single(w.Storage.Files[fileId].Links));

        // A second Shipped order confirmed without a photo touches storage not at all.
        w.Pos.Db.Orders.Add(new Order
        {
            OrderId = 4822, OrderNumber = "ORD-20261120-0002", OrderType = "Institutional", OrderSource = "POS", LocationId = 8,
            PaymentMethod = "Cash", PaymentStatus = "Paid", OrderStatus = "Shipped", TotalAmount = 150m,
            CreatedAt = DateTime.UtcNow.AddDays(-1), UpdatedAt = DateTime.UtcNow,
        });
        w.Pos.Db.SaveChanges();
        var calls = w.Storage.Calls.Count;
        var plain = await w.Orders.ConfirmInstitutionalDeliveryAsync(4822, new ConfirmInstitutionalDeliveryDto { ReceivedBy = "Front desk", DeliveredAt = DateTime.UtcNow });
        Assert.False(plain!.DeliveryConfirmation!.HasProofImage);
        Assert.Equal(calls, w.Storage.Calls.Count);
    }

    // ── Reads ──

    [Fact]
    public async Task Legacy_photos_still_open_and_storage_trouble_is_not_reported_as_missing()
    {
        using var w = new World();
        w.Pos.Db.OrderRefundProofs.Add(new OrderRefundProof { OrderId = 4821, ProofImage = "data:image/jpeg;base64,AAAA" });
        w.Pos.Db.SaveChanges();
        w.Pos.SignIn(Manager);

        var legacy = await w.Orders.GetRefundProofImageAsync(4821);
        Assert.Equal(new ProofImageResult("data:image/jpeg;base64,AAAA", ProofImageResult.LegacySource, false), legacy);
        Assert.Empty(w.Storage.Calls);
        Assert.Null(await w.Orders.GetDeliveryProofImageAsync(4821));

        var row = await w.Pos.Db.OrderRefundProofs.SingleAsync();
        row.ProofImage = null;
        row.ProofFileId = Guid.NewGuid(); // a File storage doesn't have
        w.Pos.Db.SaveChanges();
        var gone = await Assert.ThrowsAsync<ProofImageUnavailableException>(() => w.Orders.GetRefundProofImageAsync(4821));
        Assert.Equal(404, gone.Status);

        w.Storage.Down = true;
        var down = await Assert.ThrowsAsync<ProofImageUnavailableException>(() => w.Orders.GetRefundProofImageAsync(4821));
        Assert.Equal(503, down.Status);
    }

    [Fact]
    public async Task Order_lists_flag_either_representation_without_photos_or_addresses()
    {
        using var w = new World();
        w.Pos.SignIn(Cashier);
        var (reference, _) = await w.UploadAsync(ProofKinds.Refund);
        w.Storage.Down = true;
        await w.Orders.RequestRefundAsync(4821, "Spoiled", null, null, reference);
        w.Pos.SignIn(Manager);

        var listed = Assert.Single(await w.Orders.GetAllOrdersAsync(new OrderFilterDto()));

        Assert.True(listed.HasRefundProof);
        Assert.True(listed.RefundProofPending);
        var json = System.Text.Json.JsonSerializer.Serialize(listed);
        Assert.DoesNotContain("bucket.test", json);
        Assert.DoesNotContain("v1.", json);
    }
}

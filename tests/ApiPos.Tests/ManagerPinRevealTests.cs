using System.Net;
using Api.Controllers;
using Api.Middlewares;
using Applications.Interfaces;
using Applications.Services;
using Domains.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;

namespace ApiPos.Tests;

/// <summary>Ticket #15: a manager's PIN is revealed only after their account password is confirmed.</summary>
public sealed class ManagerPinRevealTests
{
    private static CurrentUserContext Manager()
    {
        var manager = PosHarness.Staff("portal-user-42", "Mira Reyes", "Manager", 8);
        manager.Email = "mira@example.test";
        return manager;
    }

    private sealed class FakeVerifier(PasswordCheckResult result) : IAccountPasswordVerifier
    {
        public List<(string Email, string Password)> Calls { get; } = [];

        public Task<PasswordCheckResult> VerifyAsync(string email, string password, CancellationToken cancellationToken)
        {
            Calls.Add((email, password));
            return Task.FromResult(result);
        }
    }

    private static PosUserPinController Controller(PosHarness pos, IAccountPasswordVerifier verifier, IMemoryCache? cache = null) =>
        new(pos.Db, verifier, cache ?? new MemoryCache(new MemoryCacheOptions()))
        {
            ControllerContext = new ControllerContext { HttpContext = pos.Accessor.HttpContext! },
        };

    private static int StatusOf(IActionResult result) => result switch
    {
        ObjectResult objectResult => objectResult.StatusCode ?? 200,
        StatusCodeResult statusResult => statusResult.StatusCode,
        _ => throw new InvalidOperationException(result.GetType().Name),
    };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task An_empty_password_is_refused_without_a_check(string? password)
    {
        using var pos = new PosHarness();
        pos.SignIn(Manager());
        var verifier = new FakeVerifier(PasswordCheckResult.Verified);

        var result = await Controller(pos, verifier).RevealUserPin(new RevealPinRequest(password), default);

        Assert.Equal(400, StatusOf(result));
        Assert.Empty(verifier.Calls);
        Assert.Empty(pos.Db.PosUserPins);
    }

    [Fact]
    public async Task An_incorrect_password_returns_no_pin()
    {
        using var pos = new PosHarness();
        pos.Db.PosUserPins.Add(new PosUserPin { AuthUserId = "portal-user-42", PinCode = "482913", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        pos.Db.SaveChanges();
        pos.SignIn(Manager());

        var result = await Controller(pos, new FakeVerifier(PasswordCheckResult.Incorrect)).RevealUserPin(new RevealPinRequest("wrong"), default);

        var refused = Assert.IsType<UnprocessableEntityObjectResult>(result);
        Assert.DoesNotContain("482913", System.Text.Json.JsonSerializer.Serialize(refused.Value));
        Assert.Contains("Incorrect password. Try again.", System.Text.Json.JsonSerializer.Serialize(refused.Value));
    }

    [Fact]
    public async Task Unavailable_verification_returns_no_pin()
    {
        using var pos = new PosHarness();
        pos.SignIn(Manager());

        var result = await Controller(pos, new FakeVerifier(PasswordCheckResult.Unavailable)).RevealUserPin(new RevealPinRequest("secret"), default);

        Assert.Equal(503, StatusOf(result));
        Assert.Empty(pos.Db.PosUserPins);
    }

    [Fact]
    public async Task A_correct_password_reveals_the_signed_in_managers_own_pin()
    {
        using var pos = new PosHarness();
        pos.Db.PosUserPins.AddRange(
            new PosUserPin { AuthUserId = "portal-user-42", PinCode = "482913", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new PosUserPin { AuthUserId = "portal-user-43", PinCode = "111222", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        pos.Db.SaveChanges();
        pos.SignIn(Manager());
        var verifier = new FakeVerifier(PasswordCheckResult.Verified);

        var result = await Controller(pos, verifier).RevealUserPin(new RevealPinRequest("secret"), default);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Contains("482913", System.Text.Json.JsonSerializer.Serialize(ok.Value));
        // The session decides whose password is checked, never the request body.
        Assert.Equal(("mira@example.test", "secret"), Assert.Single(verifier.Calls));
        Assert.Equal("no-store", pos.Accessor.HttpContext!.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task A_cashier_cannot_reveal_a_pin()
    {
        using var pos = new PosHarness();
        var cashier = PosHarness.Staff("portal-cashier-7", "Ana Cruz", "Cashier", 8);
        cashier.Email = "ana@example.test";
        pos.SignIn(cashier);
        var verifier = new FakeVerifier(PasswordCheckResult.Verified);

        var result = await Controller(pos, verifier).RevealUserPin(new RevealPinRequest("secret"), default);

        Assert.Equal(403, StatusOf(result));
        Assert.Empty(verifier.Calls);
    }

    [Fact]
    public async Task Without_a_session_no_pin_is_returned()
    {
        using var pos = new PosHarness();
        var verifier = new FakeVerifier(PasswordCheckResult.Verified);

        var result = await Controller(pos, verifier).RevealUserPin(new RevealPinRequest("secret"), default);

        Assert.Equal(401, StatusOf(result));
        Assert.Empty(verifier.Calls);
    }

    [Fact]
    public async Task Repeated_wrong_passwords_lock_the_reveal()
    {
        using var pos = new PosHarness();
        pos.SignIn(Manager());
        var cache = new MemoryCache(new MemoryCacheOptions());
        var verifier = new FakeVerifier(PasswordCheckResult.Incorrect);

        for (var i = 0; i < PosUserPinController.MaxFailedAttempts; i++)
            Assert.Equal(422, StatusOf(await Controller(pos, verifier, cache).RevealUserPin(new RevealPinRequest("wrong"), default)));

        var locked = await Controller(pos, verifier, cache).RevealUserPin(new RevealPinRequest("wrong"), default);

        Assert.Equal(429, StatusOf(locked));
        Assert.Equal(PosUserPinController.MaxFailedAttempts, verifier.Calls.Count);
    }
}

public sealed class NeonAuthPasswordVerifierTests
{
    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request, body));
            return respond(request);
        }
    }

    private static IConfiguration Config(string? baseUrl = "https://auth.neon.test/neondb/auth", string? portal = "https://portal.test") =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NEON_AUTH_BASE_URL"] = baseUrl,
            ["PORTAL_URL"] = portal,
        }).Build();

    private static NeonAuthPasswordVerifier Verifier(HttpMessageHandler handler, IConfiguration? config = null) =>
        new(new StubHttpClientFactory(handler), config ?? Config(), NullLogger.For<NeonAuthPasswordVerifier>());

    [Fact]
    public async Task A_correct_password_is_verified_and_its_session_signed_out()
    {
        var handler = new RecordingHandler(request =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            if (request.RequestUri!.AbsolutePath.EndsWith("/sign-in/email"))
            {
                response.Headers.Add("Set-Cookie", "__Secure-neon-auth.session_token=abc.def; Path=/; HttpOnly; Secure");
                response.Headers.Add("Set-Cookie", "unrelated=1; Path=/");
            }
            return response;
        });

        var result = await Verifier(handler).VerifyAsync("mira@example.test", "secret", default);

        Assert.Equal(PasswordCheckResult.Verified, result);
        Assert.Equal(2, handler.Requests.Count);
        var (signIn, signInBody) = handler.Requests[0];
        Assert.Equal("https://auth.neon.test/neondb/auth/sign-in/email", signIn.RequestUri!.ToString());
        Assert.Equal("https://portal.test", signIn.Headers.GetValues("Origin").Single());
        Assert.Contains("\"email\":\"mira@example.test\"", signInBody);
        // The password travels only in the body, never in the URL.
        Assert.DoesNotContain("secret", signIn.RequestUri.ToString());
        var (signOut, _) = handler.Requests[1];
        Assert.Equal("https://auth.neon.test/neondb/auth/sign-out", signOut.RequestUri!.ToString());
        Assert.Equal("__Secure-neon-auth.session_token=abc.def", signOut.Headers.GetValues("Cookie").Single());
    }

    [Fact]
    public async Task A_wrong_password_is_incorrect()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        Assert.Equal(PasswordCheckResult.Incorrect, await Verifier(handler).VerifyAsync("mira@example.test", "wrong", default));
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Other_answers_are_unavailable(HttpStatusCode status)
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(status));

        Assert.Equal(PasswordCheckResult.Unavailable, await Verifier(handler).VerifyAsync("mira@example.test", "secret", default));
    }

    [Fact]
    public async Task A_network_failure_is_unavailable()
    {
        var handler = new RecordingHandler(_ => throw new HttpRequestException("down"));

        Assert.Equal(PasswordCheckResult.Unavailable, await Verifier(handler).VerifyAsync("mira@example.test", "secret", default));
    }

    [Fact]
    public async Task Missing_configuration_is_unavailable_without_a_call()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var result = await Verifier(handler, Config(baseUrl: null)).VerifyAsync("mira@example.test", "secret", default);

        Assert.Equal(PasswordCheckResult.Unavailable, result);
        Assert.Empty(handler.Requests);
    }
}

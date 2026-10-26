using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Xunit;

public sealed class WebhookTests
{
    [Fact]
    public async Task Valid_signature_persists_verified_event()
    {
        using var factory = new TestApplication();
        using var client = factory.CreateClient();
        using var request = Delivery();
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(body.RootElement.GetProperty("duplicate").GetBoolean());
        var row = Assert.Single(await factory.Rows());
        Assert.Equal("WH-SYNTHETIC-001", row.EventId);
        using var payload = JsonDocument.Parse(row.Payload);
        Assert.Equal("PAYMENT.CAPTURE.COMPLETED",
            payload.RootElement.GetProperty("event_type").GetString());
        Assert.Equal(1, factory.PayPal.VerificationCalls);
    }

    [Fact]
    public async Task Invalid_signature_returns_401_without_saving()
    {
        using var factory = new TestApplication();
        factory.PayPal.Valid = false;
        using var client = factory.CreateClient();
        using var request = Delivery();
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await factory.Rows());
        Assert.Equal(1, factory.PayPal.VerificationCalls);
    }

    [Fact]
    public async Task Duplicate_is_verified_again_but_not_saved_again()
    {
        using var factory = new TestApplication();
        using var client = factory.CreateClient();
        using var firstRequest = Delivery();
        using var firstResponse = await client.SendAsync(firstRequest);
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        var original = Assert.Single(await factory.Rows());

        using var secondRequest = Delivery();
        using var secondResponse = await client.SendAsync(secondRequest);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        using var body = JsonDocument.Parse(await secondResponse.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("duplicate").GetBoolean());
        var retained = Assert.Single(await factory.Rows());
        Assert.Equal(original.ReceivedAtUtc, retained.ReceivedAtUtc);
        Assert.Equal(original.Payload, retained.Payload);
        Assert.Equal(2, factory.PayPal.VerificationCalls);
    }

    [Fact]
    public async Task Network_error_returns_503_without_saving()
    {
        using var factory = new TestApplication();
        factory.PayPal.NetworkFailure = true;
        using var client = factory.CreateClient();
        using var request = Delivery();
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Empty(await factory.Rows());
        Assert.Equal(1, factory.PayPal.VerificationCalls);
    }

    private static HttpRequestMessage Delivery()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/paypal")
        {
            Content = new StringContent(
                "{\"id\":\"WH-SYNTHETIC-001\",\"event_type\":\"PAYMENT.CAPTURE.COMPLETED\"}",
                Encoding.UTF8, "application/json")
        };
        request.Headers.Add("PAYPAL-AUTH-ALGO", "SHA256withRSA");
        request.Headers.Add("PAYPAL-CERT-URL", "https://api.paypal.com/synthetic-cert");
        request.Headers.Add("PAYPAL-TRANSMISSION-ID", "synthetic-transmission");
        request.Headers.Add("PAYPAL-TRANSMISSION-SIG", "synthetic-signature");
        request.Headers.Add("PAYPAL-TRANSMISSION-TIME", "2026-10-26T00:00:00Z");
        return request;
    }
}

internal sealed class TestApplication : WebApplicationFactory<Program>
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    public FakePayPalHandler PayPal { get; } = new();

    public TestApplication() => connection.Open();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<WebhookDb>>();
            services.RemoveAll<WebhookDb>();
            services.AddDbContext<WebhookDb>(options => options.UseSqlite(connection));
            services.RemoveAll<IPayPalVerifier>();
            services.AddScoped<IPayPalVerifier>(_ => new PayPalVerifier(
                new HttpClient(PayPal, disposeHandler: false)
                {
                    BaseAddress = new Uri("https://paypal.invalid/")
                },
                Options.Create(new PayPalOptions
                {
                    ClientId = "synthetic-client",
                    ClientSecret = "synthetic-secret",
                    WebhookId = "synthetic-webhook"
                })));
        });
    }

    public async Task<List<WebhookEvent>> Rows()
    {
        using var scope = Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WebhookDb>()
            .Events.AsNoTracking().ToListAsync();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            connection.Dispose();
            PayPal.Dispose();
        }
    }
}

internal sealed class FakePayPalHandler : HttpMessageHandler
{
    public bool Valid { get; set; } = true;
    public bool NetworkFailure { get; set; }
    public int VerificationCalls { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Assert.Equal(HttpMethod.Post, request.Method);
        if (request.RequestUri!.AbsolutePath == "/v1/oauth2/token")
        {
            Assert.Equal("Basic", request.Headers.Authorization?.Scheme);
            Assert.Equal("grant_type=client_credentials",
                await request.Content!.ReadAsStringAsync(cancellationToken));
            return Json("{\"access_token\":\"synthetic-token\"}");
        }

        Assert.Equal("/v1/notifications/verify-webhook-signature",
            request.RequestUri.AbsolutePath);
        VerificationCalls++;
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("synthetic-token", request.Headers.Authorization?.Parameter);
        using var document = JsonDocument.Parse(
            await request.Content!.ReadAsStringAsync(cancellationToken));
        var body = document.RootElement;
        Assert.Equal("synthetic-webhook", body.GetProperty("webhook_id").GetString());
        Assert.Equal("SHA256withRSA", body.GetProperty("auth_algo").GetString());
        Assert.Equal("https://api.paypal.com/synthetic-cert",
            body.GetProperty("cert_url").GetString());
        Assert.Equal("synthetic-transmission", body.GetProperty("transmission_id").GetString());
        Assert.Equal("synthetic-signature", body.GetProperty("transmission_sig").GetString());
        Assert.Equal("2026-10-26T00:00:00Z", body.GetProperty("transmission_time").GetString());
        Assert.Equal("WH-SYNTHETIC-001",
            body.GetProperty("webhook_event").GetProperty("id").GetString());
        if (NetworkFailure)
            throw new HttpRequestException("Simulated connection failure.");
        return Json(Valid
            ? "{\"verification_status\":\"SUCCESS\"}"
            : "{\"verification_status\":\"FAILURE\"}");
    }

    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };
}

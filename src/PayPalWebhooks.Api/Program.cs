using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<PayPalOptions>(builder.Configuration.GetSection("PayPal"));
builder.Services.AddDbContext<WebhookDb>(options => options.UseSqlite(
    builder.Configuration.GetConnectionString("Webhooks") ?? "Data Source=webhooks.db"));
builder.Services.AddHttpClient<IPayPalVerifier, PayPalVerifier>(client =>
{
    client.BaseAddress = new Uri("https://api-m.sandbox.paypal.com/");
    client.Timeout = TimeSpan.FromSeconds(15);
});

var app = builder.Build();
using (var scope = app.Services.CreateScope())
{
    // Sample bootstrap only. Use provider-specific migrations in production.
    scope.ServiceProvider.GetRequiredService<WebhookDb>().Database.EnsureCreated();
}

app.MapPost("/webhooks/paypal", WebhookEndpoint.Handle);
app.Run();

public partial class Program { }

public sealed class PayPalOptions
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string WebhookId { get; set; } = "";
}

public sealed class WebhookEvent
{
    public string EventId { get; set; } = "";
    public string Payload { get; set; } = "";
    public DateTimeOffset ReceivedAtUtc { get; set; }
}

public sealed class WebhookDb(DbContextOptions<WebhookDb> options) : DbContext(options)
{
    public DbSet<WebhookEvent> Events => Set<WebhookEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<WebhookEvent>();
        entity.ToTable("WebhookInbox");
        entity.HasKey(row => row.EventId);
        entity.Property(row => row.EventId).HasMaxLength(255).IsRequired();
        entity.Property(row => row.Payload).IsRequired();
    }
}

public sealed record SignatureHeaders(
    string AuthAlgo, string CertUrl, string TransmissionId,
    string TransmissionSig, string TransmissionTime);

public interface IPayPalVerifier
{
    Task<bool> VerifyAsync(JsonElement webhookEvent, SignatureHeaders headers,
        CancellationToken cancellationToken);
}

public sealed class PayPalUnavailableException(string message) : Exception(message);

public sealed class PayPalVerifier(HttpClient client, IOptions<PayPalOptions> options)
    : IPayPalVerifier
{
    public async Task<bool> VerifyAsync(JsonElement webhookEvent, SignatureHeaders headers,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.ClientId) ||
            string.IsNullOrWhiteSpace(settings.ClientSecret) ||
            string.IsNullOrWhiteSpace(settings.WebhookId))
            throw new PayPalUnavailableException("PayPal configuration is missing.");

        using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, "v1/oauth2/token");
        tokenRequest.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(
                settings.ClientId + ":" + settings.ClientSecret)));
        tokenRequest.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials"
        });
        using var tokenResponse = await client.SendAsync(tokenRequest, cancellationToken);
        if (!tokenResponse.IsSuccessStatusCode)
            throw new PayPalUnavailableException("PayPal authentication failed.");
        using var tokenDocument = await JsonDocument.ParseAsync(
            await tokenResponse.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        if (!tokenDocument.RootElement.TryGetProperty("access_token", out var token) ||
            token.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(token.GetString()))
            throw new PayPalUnavailableException("PayPal returned no access token.");

        using var verificationRequest = new HttpRequestMessage(HttpMethod.Post,
            "v1/notifications/verify-webhook-signature");
        verificationRequest.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", token.GetString());
        verificationRequest.Content = JsonContent.Create(new
        {
            auth_algo = headers.AuthAlgo,
            cert_url = headers.CertUrl,
            transmission_id = headers.TransmissionId,
            transmission_sig = headers.TransmissionSig,
            transmission_time = headers.TransmissionTime,
            webhook_id = settings.WebhookId,
            webhook_event = webhookEvent
        });
        using var response = await client.SendAsync(verificationRequest, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new PayPalUnavailableException("PayPal signature verification is unavailable.");
        using var verification = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        if (!verification.RootElement.TryGetProperty("verification_status", out var status) ||
            status.ValueKind != JsonValueKind.String)
            throw new PayPalUnavailableException("PayPal returned an invalid verification response.");
        return status.GetString() switch
        {
            "SUCCESS" => true,
            "FAILURE" => false,
            _ => throw new PayPalUnavailableException("Unknown PayPal verification status.")
        };
    }
}

public static class WebhookEndpoint
{
    public static async Task<IResult> Handle(HttpRequest request, WebhookDb db,
        IPayPalVerifier verifier, CancellationToken cancellationToken)
    {
        var names = new[]
        {
            "PAYPAL-AUTH-ALGO", "PAYPAL-CERT-URL", "PAYPAL-TRANSMISSION-ID",
            "PAYPAL-TRANSMISSION-SIG", "PAYPAL-TRANSMISSION-TIME"
        };
        if (names.Any(name => request.Headers[name].Count != 1 ||
            string.IsNullOrWhiteSpace(request.Headers[name].ToString())))
            return Results.BadRequest(new { error = "Missing or repeated signature headers." });

        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(request.Body,
                cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { error = "Invalid event JSON." });
        }

        using (document)
        {
            var payload = document.RootElement;
            if (payload.ValueKind != JsonValueKind.Object ||
                !payload.TryGetProperty("id", out var id) ||
                id.ValueKind != JsonValueKind.String)
                return Results.BadRequest(new { error = "Missing event id." });
            var eventId = id.GetString()!;
            if (string.IsNullOrWhiteSpace(eventId) || eventId.Length > 255)
                return Results.BadRequest(new { error = "Invalid event id." });

            var headers = new SignatureHeaders(
                request.Headers[names[0]].ToString(), request.Headers[names[1]].ToString(),
                request.Headers[names[2]].ToString(), request.Headers[names[3]].ToString(),
                request.Headers[names[4]].ToString());
            try
            {
                // Verify every delivery, including duplicates, before database access.
                if (!await verifier.VerifyAsync(payload, headers, cancellationToken))
                    return Results.Unauthorized();
            }
            catch (Exception exception) when (
                exception is HttpRequestException or PayPalUnavailableException or JsonException ||
                (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            if (await db.Events.AsNoTracking().AnyAsync(
                row => row.EventId == eventId, cancellationToken))
                return Results.Ok(new { duplicate = true });

            db.Events.Add(new WebhookEvent
            {
                EventId = eventId, Payload = payload.GetRawText(),
                ReceivedAtUtc = DateTimeOffset.UtcNow
            });
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException exception) when (
                exception.InnerException is SqliteException sqlite &&
                sqlite.SqliteErrorCode == 19 &&
                (sqlite.SqliteExtendedErrorCode == 1555 || sqlite.SqliteExtendedErrorCode == 2067))
            {
                // The primary key also protects simultaneous deliveries.
                db.ChangeTracker.Clear();
                if (!await db.Events.AsNoTracking().AnyAsync(
                    row => row.EventId == eventId, cancellationToken))
                    throw;
                return Results.Ok(new { duplicate = true });
            }
            // The inbox row is the durable acceptance boundary. No external side effects.
            return Results.Ok(new { duplicate = false });
        }
    }
}

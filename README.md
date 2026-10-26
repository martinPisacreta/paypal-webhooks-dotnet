# PayPal Webhooks — ASP.NET Core 8

[![.NET integration tests](https://github.com/martinPisacreta/paypal-webhooks-dotnet/actions/workflows/dotnet.yml/badge.svg)](https://github.com/martinPisacreta/paypal-webhooks-dotnet/actions/workflows/dotnet.yml)

Minimal portfolio sample demonstrating PayPal webhook signature verification and durable, idempotent event acceptance with EF Core and SQLite.

## Run the tests

Requires the .NET 8 SDK. From the repository root:

```sh
dotnet test PayPalWebhooks.sln --configuration Release
```

Four xUnit integration tests: valid signature, invalid signature, duplicate delivery, and network failure. Tests exercise the HTTP endpoint through an in-process test server, use SQLite in memory, and replace PayPal's HTTP transport with a simulated handler. They check the verification request fields and persisted rows. No real PayPal credentials or API calls are used. Package restoration requires NuGet access.

## Request flow

`POST /webhooks/paypal` accepts an event object containing `id` and the five PayPal signature headers. An injectable HTTP client obtains an OAuth token and posts the event, headers and configured webhook ID to `/v1/notifications/verify-webhook-signature`. Every delivery is verified, including duplicates. The certificate URL is forwarded as data; the application does not fetch it.

| Outcome | HTTP response | Database effect |
| --- | --- | --- |
| Verified new event | 200, duplicate=false | Insert one inbox row |
| Verified duplicate | 200, duplicate=true | Retain the original row |
| Invalid signature | 401 | None |
| PayPal network or verification service failure | 503 | None |
| Invalid JSON, event ID or signature headers | 400 | None |

`WebhookInbox.EventId` is the primary key and idempotency boundary. A duplicate lookup handles repeated deliveries; the database constraint also prevents duplicate inserts. This sample persists verified events without executing payment or fulfillment side effects.

## Local configuration

The application defaults to PayPal's sandbox API and a local `webhooks.db` SQLite database. Start it with:

```sh
dotnet run --project src/PayPalWebhooks.Api
```

Configuration keys are `PayPal:ClientId`, `PayPal:ClientSecret`, `PayPal:WebhookId`, and `ConnectionStrings:Webhooks`. Supply secrets through an appropriate external configuration provider; none are included. Missing PayPal configuration causes webhook verification to return 503. Running the API against PayPal requires separately supplied credentials and is outside this sample's validation.

## SQL Server and production scope

The EF entity and DbContext keep persistence separate from verification. To use SQL Server, replace the SQLite provider and `UseSqlite` registration with `Microsoft.EntityFrameworkCore.SqlServer` and `UseSqlServer`, create provider-specific migrations, and adapt duplicate-key handling to SQL Server errors 2601/2627. The sample uses `EnsureCreated` for local bootstrap; replace it with migrations for deployment.

The tests establish the four simulated acceptance paths. They do not establish real PayPal signature acceptance, SQL Server compatibility, concurrent-delivery behavior, or exactly-once business side effects. Production extensions include a transactional inbox worker, bounded request sizes, retention controls, and operational monitoring.

## License

MIT; see `LICENSE`.

# Vairas Notifications

Durable in-app and email notifications for lesson and learning events (ASP.NET Core 10, PostgreSQL via Npgsql with
row-level security). It consumes Scheduling and Learning events through Service Bus subscriptions, resolves
recipients and contact data from the owning services at send time, and delivers through the Gmail API with an
attempt ledger and reconciliation of uncertain sends.

## Projects

| Project | Responsibility |
|---|---|
| `Vairas.Notifications` | HTTP API: controllers, `Configuration/Configure*`, security, owner clients, delivery and inbox workers |
| `Vairas.Notifications.Business` | Notification service, delivery workflow, contracts |
| `Vairas.Notifications.Domain` | Notification entities |
| `Vairas.Notifications.Persistance` | Npgsql stores, migrator, content retention |
| `Vairas.Notifications.Tests` | NUnit tests (PostgreSQL via Testcontainers, optional broker tests) |

## Dependencies

PostgreSQL 17, Authentication, User Management, School Management, Scheduling, Learning; Service Bus (emulator
locally) and the Gmail API (a local wire double in the e2e stack). Local origin `https://localhost:7600`.

## Essential configuration

Options are bound and validated at startup (`Configuration/ConfigureOptions.cs`). Use `__` in environment variables.

| Section | Notes |
|---|---|
| `ConnectionStrings:Notifications` | required, restricted runtime role |
| `ConnectionStrings:Delivery` | separate worker role; required only when `Delivery:Enabled` or `ServiceBus:Enabled` |
| `Authentication` | HTTPS `Authority`/`Issuer`, `UserAudience` |
| `Dependencies` | HTTPS origins of the owning services, workload `ClientId`/`ClientSecret`/`Scope` |
| `Delivery` | `Enabled`, `ApplicationOrigin` (HTTPS origin), bounds; enabling requires `Gmail:Enabled` |
| `Gmail` | `Enabled`, `Sender`, OAuth credentials; endpoint overrides are accepted only in Development |
| `ServiceBus` | `Enabled`, namespace or connection string, topics, subscription |

## Run locally

```powershell
dotnet run --project Vairas.Notifications --launch-profile https
dotnet run --project Vairas.Notifications -- --initialize   # operator command: provision, migrate, grant
```

The complete local stack is started from `../local/Start.ps1`.

## Tests

```powershell
dotnet test Vairas.Notifications.slnx            # Docker required; broker tests also need VAIRAS_TEST_SERVICEBUS_CONNECTION
./scripts/verify.ps1 -SkipIntegration            # unit tests with the coverage gate
./scripts/test-broker.ps1                        # Service Bus emulator tests
```

API exports: `api/`. Extended notes, Azure/deployment material, validation reports and evidence:
`../../Documentation/Backend/Vairas.Notifications/`.

## Tests and coverage

Requirements: .NET SDK 10 and Docker (PostgreSQL integration tests start a throw-away container via Testcontainers).

```powershell
dotnet test Vairas.Notifications.slnx                  # unit + integration tests
dotnet test Vairas.Notifications.slnx --collect:"XPlat Code Coverage" --settings coverage.runsettings --results-directory TestResults   # measure coverage
```

Broker tests (category `Broker`) need the Azure Service Bus emulator (Docker, Microsoft EULA): run `./scripts/test-broker.ps1 -AcceptMicrosoftEula` or set `VAIRAS_TEST_SERVICEBUS_CONNECTION`; exclude them with `--filter "TestCategory!=Broker"`.

**Coverage report:** [docs/coverage/README.md](docs/coverage/README.md) (summary, per-assembly table, least covered files); machine-readable `docs/coverage/coverage-summary.json` and `docs/coverage/index.html` (per-file table). The raw cobertura XML of a local run is written to `TestResults/`.
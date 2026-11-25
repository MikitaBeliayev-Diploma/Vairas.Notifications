# Vairas.Notifications — code coverage

**Line coverage 84.62 %** (1249/1476 executable lines), branch coverage 65.58 % — threshold 70 % **met**. Generated 2026-11-25.

| Assembly | Line % | Lines | Branch % |
|---|---:|---:|---:|
| Vairas.Notifications | 73.25 | 534/729 | 53.52 |
| Vairas.Notifications.Business | 95.09 | 329/346 | 70.3 |
| Vairas.Notifications.Domain | 100.0 | 87/87 | 92.13 |
| Vairas.Notifications.Persistance | 95.22 | 299/314 | 71.58 |

## How it was measured

- Tool: coverlet (`XPlat Code Coverage`) driven by `Backend/local/coverage.py`; settings `Vairas.Notifications/coverage.runsettings` (migrations and test assemblies excluded).
- Test projects: `Vairas.Notifications.Tests`; line hits of all test projects of the service are merged (maximum per line), so shared code is never counted twice.
- Filter: `TestCategory!=Broker`. Broker tests need the Service Bus emulator and are executed (and passed) separately by `local/test_all.py`.
- The numbers are measured line coverage, not a statement about functional completeness; acceptance status is tracked in `Documentation/Final-Verification/AC-Traceability-Matrix.xlsx`.

## Reproduce

```powershell
cd Backend
py -3 local/coverage.py --services Vairas.Notifications --filter "TestCategory!=Broker" --threshold 70
```

## Files

`coverage-summary.json` (machine-readable, per file) and `index.html` (per-file table, lowest coverage first) are in this folder.

## Least covered files

| File | Line % | Lines |
|---|---:|---:|
| `Vairas.Notifications.Business/UnprocessableEntityException.cs` | 0.0 | 0/1 |
| `Workers/NotificationTelemetry.cs` | 0.0 | 0/7 |
| `Startup/DatabaseProvisioning.cs` | 0.0 | 0/24 |
| `Vairas.Notifications.Persistance/PersistenceFailureException.cs` | 0.0 | 0/1 |
| `Workers/InboxWorker.cs` | 7.46 | 5/67 |
| `Workers/DeliveryWorker.cs` | 13.16 | 5/38 |
| `HealthChecks/WorkerReadiness.cs` | 21.74 | 5/23 |
| `Startup/StartupCommands.cs` | 26.67 | 8/30 |

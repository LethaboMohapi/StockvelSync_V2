# StokvelSync

[![Version v1.0.0](https://img.shields.io/badge/version-v1.0.0-007ec6)](https://github.com/LethaboMohapi/StockvelSync_V2)
[![Application checks](https://img.shields.io/github/actions/workflow/status/LethaboMohapi/StockvelSync_V2/ci.yml?branch=master&label=Application%20checks&logo=github)](https://github.com/LethaboMohapi/StockvelSync_V2/actions/workflows/ci.yml)
[![License MIT](https://img.shields.io/badge/license-MIT-007ec6)](LICENSE)
[![.NET 10.0](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
[![MySQL 8.0+](https://img.shields.io/badge/MySQL-8.0%2B-4479A1?logo=mysql&logoColor=white)](https://dev.mysql.com/doc/)

C# / ASP.NET Core MVC with Razor views, plain JavaScript, and MySQL. Includes an MIT License. This is the current source snapshot; further checks and fixes were stopped at the user's request.

## Open in Visual Studio

1. Extract the ZIP first.
2. Open **StokvelSync_V2.sln** in **Visual Studio 2026** with the **ASP.NET and web development** workload and .NET 10 SDK installed.
3. Set **StokvelSync.Web** as the startup project.
4. Complete the MySQL setup below, then press **F5**.
5. The app opens at **http://localhost:5080**. The sample-data demonstration is at **http://localhost:5080/demo**.

## Open in VS Code

1. Extract the ZIP and use **File → Open Folder** to open the **StokvelSync** folder containing `StokvelSync_V2.sln`.
2. Install the recommended **C# Dev Kit** extension and .NET 10 SDK.
3. Complete the MySQL setup below.
4. In the integrated terminal, from the solution folder, run:

```powershell
dotnet restore StokvelSync_V2.sln
dotnet run --project src/StokvelSync.Web
```

Open **http://localhost:5080**. An F5 launch configuration is also included in `.vscode`.

## MySQL setup for local development

Use MySQL 8.0 or 8.4. Open MySQL Workbench and run `database/create-database.sql` as a database administrator. It creates database **`StockvelSync_V2`** and MySQL user **`StockvelSync_V2`**, separately from the first attempt. It does not rename or alter the first database or user.

Copy the example local configuration:

```powershell
Copy-Item src/StokvelSync.Web/appsettings.Local.example.json src/StokvelSync.Web/appsettings.Local.json
```

Edit the MySQL connection string in `src/StokvelSync.Web/appsettings.Local.json` to match your server, port, user and password. The example uses port **3306** and the local development password in the SQL setup script. Keep this local file out of source control. Both `Database` and `User ID` must be `StockvelSync_V2`. Update any existing local configuration or `ConnectionStrings__MySql` environment variable that still points to the first attempt. If the V2 MySQL user already exists, use its existing password; the setup script does not reset it.

Create the tables, optionally load the sample stokvel, then run:

```powershell
dotnet run --project src/StokvelSync.Web -- --migrate
dotnet run --project src/StokvelSync.Web -- --seed
dotnet run --project src/StokvelSync.Web
```

Seeding is optional, only runs in Development, and requires an empty database. The seeded treasurer signs in with **0820000001** and **StokvelDemo!2026**. A member signs in with **0820000002** and the same local sample password. You can set `SEED_DEMO_PASSWORD` before seeding to choose a different sample password. The browser demo uses separate sample data stored on the device.

## Docker alternative

With Docker Desktop running, from the solution folder:

```powershell
Copy-Item .env.example .env
docker compose up --build
```

The app is at **http://localhost:5080**. MySQL is exposed on **127.0.0.1:3307**; the web container connects to MySQL internally on port 3306. Optional sample data:

```powershell
docker compose exec web dotnet StokvelSync.Web.dll --seed
```

The Compose project is `stockvelsync-v2` and uses a separate `mysql_data_v2` volume. Existing containers must be recreated to apply configuration changes; do not reuse or delete the first attempt's data volume. The host ports remain 5080 and 3307, so stop the first stack or adjust the V2 port mappings if running both simultaneously.

The supplied Compose configuration is for local development. The Docker stack was not run in this session.

## Included features

- Account registration and sign-in, database-backed sessions, treasurer/member permissions, and multiple independent stokvels.
- Invitation codes, member list, contribution records, treasurer approvals, transparent confirmed ledger, and CSV export.
- English, isiZulu, isiXhosa, and Afrikaans interface text.
- Monthly rotation scheduling, adjustable order, and recording of externally completed payouts.
- Offline application shell, saved ledger, IndexedDB contribution queue, and idempotent synchronization when connected again.
- Local simulated mobile-wallet payments, Payfast hosted-checkout integration, verified payment notifications, and duplicate-payment protection.
- SMS command handling, local SMS simulator, opt-in reminders, Twilio request-signature validation, and an outbound message worker.
- MySQL schema/migration scripts, Docker configuration, C# tests, CI workflow, and editor configuration.

## Payments and SMS

Defaults are **Payments:Provider = sandbox** and **Sms:Provider = console**. These do not move money or send SMS. The browser demo also uses simulated operations.

For Payfast, set `Payments:Provider` to `payfast-sandbox` or `payfast-live`, and configure `MerchantId`, `MerchantKey`, and `Passphrase`. Set `App:PublicUrl` to your externally reachable HTTPS address. The notification endpoint is `/api/webhooks/payfast`. Payment methods, including mobile-wallet availability, depend on your Payfast merchant account. A browser return from checkout does not confirm a contribution; a validated notification does.

For Twilio, set `Sms:Provider` to `twilio` and configure `AccountSid`, `AuthToken`, `FromNumber`, and the exact `WebhookUrl`, ending in `/api/webhooks/sms`. Members set their six-digit SMS PIN and default stokvel in Settings. Commands are `BAL <PIN>`, `NEXT <PIN>`, and `PAY <PIN> <rand amount>`. `PAY` creates a pending contribution claim and does not transfer money. Real provider accounts, credentials and live transactions were not tested.

Use environment variables with double underscores for configuration, such as `ConnectionStrings__MySql`, `Payments__MerchantId`, and `Sms__AuthToken`. Do not commit real credentials.

## Offline behavior

Open the app once while connected so the service worker and account snapshot can be stored. Without data, members can read the saved ledger and record cash/EFT contribution claims. Pending local records remain separate from confirmed funds. Reconnection sends records using their original operation IDs. Approvals, payments, account changes and schedule changes require a connection. SMS requires cellular coverage and a reachable server.

Offline records are stored on the device. Do not clear browser storage before syncing. Sign out on shared devices. Bump the service-worker cache version when releasing changed static assets.

## Hosting and further development

The deployable application is the ASP.NET Core project under `src/StokvelSync.Web`. Production requires an HTTPS public URL, `App:CookieSecure=true`, a persistent MySQL database, and production credentials supplied through environment variables or a secret store. Enable forwarded headers only behind a trusted proxy and configure `App:KnownProxy` where needed.

The SMS worker marks accepted outbound messages as sent to the provider; this is not proof of handset delivery. Review uncertain `sending` records and unfinished inbound receipts before manually retrying them. The in-memory request limiter is suitable for one app instance; a shared limiter is needed when scaling across instances. There is no subscription billing or automatic bank payout service in this source snapshot.

## Tests

Tests are supplied for future use; checks were stopped as requested. `dotnet test StokvelSync_V2.sln` runs domain tests and skips the MySQL integration test unless `STOKVELSYNC_TEST_MYSQL` is set to a dedicated test database connection string, for example database `StockvelSync_V2_tests` with MySQL user `StockvelSync_V2`. CI uses this separate test database. Never point the integration suite at production data.

## Source map

- `src/StokvelSync.Web/Controllers`: page and API endpoints.
- `src/StokvelSync.Web/Core`: business rules, authentication helpers, MySQL access, payments and SMS.
- `src/StokvelSync.Web/Views/Home/Index.cshtml`: Razor application shell.
- `src/StokvelSync.Web/wwwroot`: interface, language dictionaries, offline queue, service worker and bundled fonts.
- `src/StokvelSync.Web/Database`: versioned MySQL table definitions.
- `tests/StokvelSync.Tests`: domain and database integration tests.

## License

Application source is licensed under the MIT License in `LICENSE`. Bundled DM Sans and Manrope fonts retain their own license files under `wwwroot/fonts`. External .NET and NuGet dependencies retain their respective upstream licenses.

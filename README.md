# ytOrg_cron_net

Nightly YouTube sync runner for `ytOrg3` (.NET 10 console application).

## Overview
- Triggers `POST /api/cron/sync-all` (or `/api/cron/sync-user/{userId}`) on `ytOrg3`.
- Communicates across Railway Private Networking (`http://ytappv3.railway.internal:8080`).
- Retries with linear backoff (3 attempts, 5m timeout).
- Logs transport or job failures to `ytl_v2.zerror_log_cron`.
- Exits 0 on success, 1 on failure.

## CLI Usage
```bash
# Sync all active users (default)
dotnet run

# Sync a specific user only
dotnet run -- --user-id 1
```

## Railway Deployment
- Scheduled as a Railway Cron service.
- Start command:
  ```bash
  dotnet run --project ytOrg_cron_net.csproj -- --user-id 1
  ```
- Environment variables:
  - `YT_APP_URL=http://ytappv3.railway.internal:8080`
  - `CRON_SECRET=<shared-cron-secret>`
  - `DATABASE_URL=postgresql://...` (or `DB_HOST`, `DB_PORT`, `DB_NAME`, `DB_USER`, `DB_PASSWORD`)

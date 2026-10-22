# VitalWatch.AspNetCore

ASP.NET Core reporter for [VitalWatch Hub](https://github.com/CestnyTR/vitalwatch-hub):
unhandled exceptions, 5xx responses, slow requests, slow SQL queries and crashed background
services.

.NET 8 and .NET 10. **No runtime dependencies** — only the ASP.NET Core shared framework.
It does not depend on the EF Core, SqlClient or Npgsql packages; it listens to them when they
are present.

Sibling packages: [`vitalwatch-node`](https://github.com/CestnyTR/vitalwatch-node) (Node),
[`vitalwatch`](https://github.com/CestnyTR/vitalwatch-python) (Python),
[`cestnytr/vitalwatch-laravel`](https://github.com/CestnyTR/vitalwatch-laravel) (Laravel),
`t.js` (browser, served by the hub). Same environment variables, same masking, same wire
contract.

---

## Installation

```bash
dotnet add package VitalWatch.AspNetCore
```

`Program.cs` — two lines:

```csharp
using VitalWatch;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddVitalWatch();          // 1

var app = builder.Build();
app.UseVitalWatch();                       // 2 — as early as possible

if (await app.RunVitalWatchStatusAsync(args)) return;   // optional: diagnostic command

// ... rest of the application
app.Run();
```

`UseVitalWatch()` starts timing from the point where it is added; add it right after
`builder.Build()`. Whether it comes before or after `UseExceptionHandler` does not matter
(see [Alongside an exception handler](#alongside-an-exception-handler)).

### Configuration

Environment variables — same names as the sibling packages:

```env
VITALWATCH_URL=https://vitalwatch.example.com
VITALWATCH_KEY=project-key
VITALWATCH_SECRET=<64-character secret from the panel>
VITALWATCH_ENV=production
```

or `appsettings.json`:

```json
{
  "VitalWatch": {
    "Url": "https://vitalwatch.example.com",
    "Key": "project-key",
    "Env": "production"
  }
}
```

**Do not put the secret in `appsettings.json`** — use an environment variable, user-secrets
or a key vault. A secret committed to the repository belongs to everyone who can read it.

Precedence (later overrides earlier):

1. `appsettings*.json` → `VitalWatch` section
2. `VITALWATCH_*` in the process environment — **the process environment always wins** (same rule as the sibling packages)
3. Code: `AddVitalWatch(o => { ... })`

```csharp
builder.Services.AddVitalWatch(o =>
{
    o.SlowRequestMs = 2000;
    o.Ignore.Add(typeof(OperationCanceledException));
});
```

### Verify the installation

```bash
dotnet run -- --vitalwatch-status            # show the configuration
dotnet run -- --vitalwatch-status --test     # send a real test error
dotnet run -- --vitalwatch-status --vitalwatch   # liveness request without creating an error in the panel
# in a published app:
dotnet MyApp.dll --vitalwatch-status --test
```

Sample output:

```
  SDK version        0.1.1
  Latest version     0.1.1
  Enabled            yes
  Hub URL            https://vitalwatch.example.com
  Project key        project-key
  Secret length      64 characters
  Environment        production
  Release tag        a1b2c3d4e5f6 (source: .git)
  .NET               10.0.12

  ✓ Configuration OK.
  ✓ Test event sent (HTTP 204).

  Only the hub panel can confirm that data arrives:
  the project row should show the connection status "Connected".
  The hub returns 204 even for an invalid signature; this command cannot tell.
```

The command runs in the application's **own** host, does not start the web server, and exits
(exit code 1 if something is wrong). It is not a separate `dotnet tool`, because such a tool
could not see the configuration the application sees: `appsettings.Production.json`,
user-secrets, a key vault and settings passed in code only come together in the application's
host. A diagnostic tool that gives the wrong diagnosis is worse than none. It is not an HTTP
endpoint either: that would expose the configuration to the internet.

Even without the `RunVitalWatchStatusAsync` line, if the configuration is missing or invalid
(no URL/key/secret, secret not 64 characters, `http://` URL, unrecognized environment) a
**single warning** is logged at startup.

> The hub returns 204 even for an invalid signature. The only way to confirm that data
> actually arrives is the hub panel: the project row should show the connection status
> "Connected".

`VITALWATCH_STATUS_OFFLINE=1` skips the latest-version check against NuGet.

---

## What it collects

| Type | When |
|---|---|
| `exception` | Unhandled exception in the request pipeline. Reported with the route **pattern**: `GET /api/products/{id}` |
| `http_5xx` | 5xx response without an exception (`Results.StatusCode(503)` …) |
| `slow_request` | Request exceeding `SlowRequestMs`: duration, status, query count, slowest query |
| `slow_query` | SQL command exceeding `SlowQueryMs`, literal values replaced with `?` |
| `job_failed` | Crashed `BackgroundService` / hosted service; the service type appears in the "Path" column |
| `fatal` | Out of memory; an unhandled exception that brings down the process is reported as `exception` |

Every event carries: exception class, masked message and stack trace, file and line when a pdb
is available, route pattern, method, environment, release tag, `runtime: "dotnet"`, .NET
version, SDK version.

- **One failure, one record.** A request that throws opens only an `exception` with a stack
  trace; no separate `http_5xx` is sent for the same request. The same exception object is
  sent once, whichever path it arrives through (middleware, exception handler, logging,
  manual report).
- **4xx is not sent.** `BadHttpRequestException` (400) and exceptions that the application's
  `IExceptionHandler` turns into a 4xx are not failures. Neither is a client disconnect
  (cancellation).
- **Exceptions are never swallowed.** The exception is recorded and rethrown with the same
  stack trace; your error page and `ProblemDetails` keep working as before.

### Alongside an exception handler

```csharp
app.UseVitalWatch();
app.UseExceptionHandler("/Error");   // before or after — both work
```

- If `UseVitalWatch` comes **first**, the exception never reaches the middleware; it is
  taken from the diagnostic event published by the exception handler and reported if the
  response ends with a 5xx.
- If it comes **after**, the middleware sees the exception itself and rethrows it. When the
  handler re-executes the request at `/Error`, the second pass is not measured; the panel
  shows the endpoint that failed, not `/Error`.

### Slow SQL

No setup required. Sources:

| Source | Coverage |
|---|---|
| EF Core `DiagnosticListener` | All EF providers (SQL Server, PostgreSQL, SQLite, MySQL …) |
| SqlClient `DiagnosticListener` | SQL Server via Dapper / raw ADO.NET (Microsoft.Data.SqlClient and System.Data.SqlClient) |
| Npgsql `ActivitySource` | PostgreSQL via Dapper / raw ADO.NET |

- The query count is tracked **per request** (`AsyncLocal`): concurrent requests do not see
  each other's queries. `slow_request` events include `query_count`, `slowest_query_sql` and
  `slowest_query_ms` — the way to catch N+1 problems.
- SQL is **normalized**: `where email = 'jane@example.com' and id = 42` →
  `where email = ? and id = ?`. This serves both privacy (KVKK/GDPR) and grouping: the same
  query with different parameters lands under a single fingerprint. PostgreSQL's
  double-quoted identifiers also become `?` — the sibling packages and the hub do the same.
- EF Core and SqlClient/Npgsql both report the same command; the package counts it once.
  Known trade-off: if a single request uses both EF and raw Npgsql (Dapper), raw Npgsql
  queries after the EF ones are not counted.

### Background services

When `BackgroundService.ExecuteAsync` throws, .NET publishes no hook; the only trace is the
host's own log entry. The package therefore adds a small logging provider that listens only
to **Error/Critical entries with an exception** in the
`Microsoft.Extensions.Hosting.Internal.Host` category. It does not touch your application's
other logs.

> If you use `builder.Logging.ClearProviders()`, call it **before** `AddVitalWatch()`;
> calling it afterwards also removes the package's logging provider, and `job_failed`
> events stop arriving.

### Manual reporting

```csharp
// Via injection
app.MapPost("/import", async (IVitalWatch vitalWatch) =>
{
    try { /* ... */ }
    catch (Exception e)
    {
        vitalWatch.Report(e);   // enqueues, does not wait
        return Results.Problem();
    }
});

// Outside DI
VitalWatchSdk.Report(exception);
VitalWatchSdk.Report(exception, "command_failed");
```

When called inside a request, the route and method are added automatically, and no separate
"HTTP 500" is opened for the same request. The static class is named `VitalWatchSdk`: a class
with the same name as the namespace (`VitalWatch.Report`) would not compile in a file that
has `using VitalWatch;`.

---

## What it does not collect

These cannot be enabled even through configuration — there is no code for them:

- IP address
- User-Agent
- Request body / form data
- Query string **values** (only the path is stored; the pattern for matched routes)
- Session data, cookies, the `Authorization` header
- Raw SQL literal values

Before sending, messages and stack traces are masked for email addresses, Turkish national ID
numbers, phone numbers, IBANs, card numbers and long random strings (session IDs, API keys).
Inner exception messages (such as the driver error inside a `DbUpdateException`) also go
through SQL scrubbing. The patterns are identical to the sibling packages and are tested
against the shared test vectors produced by the hub.

---

## Behavior guarantees — non-negotiable

1. **Exceptions are never swallowed.** The exception is rethrown with `throw;`, preserving the stack trace.
2. **It never reports its own errors.** All package code swallows its own errors; no infinite loops.
3. **It never blocks the user's request.** The request only enqueues the event; sending happens
   on a single background task with a short timeout. The queue is bounded (100); when full,
   events are dropped — a monitoring package must not eat the application's memory.
4. **If the hub is unreachable, it gives up silently.** No log noise, no retry storms.
5. **Nothing is installed when `VITALWATCH_ENABLED=false`** — no middleware, no logging
   provider, no diagnostic subscription, no background task. The way to turn monitoring off
   without redeploying.
6. **It does not interfere with process lifetime.** An exception that brings down the process
   is reported, but the process still goes down.

> A monitoring package breaking the application it monitors is a bigger problem than the one
> it solves.

---

## Delivery

- `POST {URL}/api/i/{KEY}/server`, with `X-VitalWatch-Signature: sha256=hex(hmac_sha256(secret, "{timestamp}." + body))`
  and `X-VitalWatch-Timestamp`. Redirects are not followed.
- **At most 8 events and 8 KB** per request — the hub silently drops the excess. For an event
  that does not fit, the stack trace is trimmed first, then the message (to 200 characters);
  if several events do not fit, the rest wait for the next request.
- Events raised at the same time are batched into one request within 50 ms (the hub limits
  each IP to 60/min).
- On application shutdown the queue is drained for at most `timeout + 0.5 s` (capped at 3 s).
- The `traceparent` header is not sent to the hub: monitoring requests do not mix into your
  application's distributed traces.

## Liveness heartbeat

Every eight hours the package sends a small request with no events (`{"events":[]}`); the
first one goes out as soon as the application starts. The hub used to infer that an
installation was working only from incoming errors, so an application running without errors
looked like a "broken installation". The interval is a third of the hub's 24-hour silence
threshold: even if two rounds are missed, no alert fires. `HeartbeatInterval =
TimeSpan.Zero` turns it off.

---

## Configuration reference

| Variable | appsettings | Default | Description |
|---|---|---|---|
| `VITALWATCH_ENABLED` | `Enabled` | `true` | If `false` (or `0`/`no`/`off`), nothing is installed |
| `VITALWATCH_URL` | `Url` | — | Hub URL |
| `VITALWATCH_KEY` | `Key` | — | Project key from the panel |
| `VITALWATCH_SECRET` | `Secret` | — | 64 characters. **Server-side only** |
| `VITALWATCH_ENV` | `Env` | ASP.NET Core environment | `production` · `staging` · `local`. Aliases are mapped: `Development`/`dev`/`test` → local, `Staging`/`stage`/`uat` → staging, `Production`/`prod`/`live` → production. The hub rejects unrecognized values; the diagnostic command reports an error |
| `VITALWATCH_RELEASE` | `Release` | automatic | Release tag, see [Release tag](#release-tag) |
| `VITALWATCH_TIMEOUT` | `Timeout` | `2000` | Send timeout. **Values of 100 and above are milliseconds, below that seconds** (`2` → 2000 ms). Clamped to 100–10000 ms; invalid, zero or negative values become 2000. Same rule in every package |
| `VITALWATCH_SLOW_REQUEST_MS` | `SlowRequestMs` | `1000` | Requests longer than this are considered slow |
| `VITALWATCH_SLOW_QUERY_MS` | `SlowQueryMs` | `500` | SQL commands longer than this are considered slow |
| — | — | — | Code only: `Ignore` (exception types to ignore, including subclasses), `HeartbeatInterval` |

### Release tag

Resolved once at startup, in this order (same rule as the sibling packages):

1. Explicit value: code → `VITALWATCH_RELEASE` → `VitalWatch:Release`. Trimmed, at most 64 characters.
2. The first non-empty CI/PaaS variable: `GIT_COMMIT`, `GIT_SHA`, `COMMIT_SHA`, `SOURCE_VERSION`,
   `VERCEL_GIT_COMMIT_SHA`, `RENDER_GIT_COMMIT`, `HEROKU_SLUG_COMMIT`, `CI_COMMIT_SHA`.
   If it looks like a sha, the first 12 characters in lowercase.
3. `.git`: `HEAD` → branch ref file, otherwise `packed-refs`; a detached HEAD is the sha itself.
   If `.git` is a file (worktree, submodule), its `gitdir:` is followed. If it is not in the
   content root, **up to three parent directories** are checked — .NET projects usually run
   from `repo/src/MyApp`. Only a valid 40-character sha is accepted; the first 12 characters
   are used.
4. If none is found, no tag is sent.

The `git` program is never executed; only a few small files are read. The diagnostic command
shows the tag along with its source.

---

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| Nothing in the panel, the app is silent | Run `dotnet run -- --vitalwatch-status --test`. Check for the startup warning "VitalWatch configuration is missing or invalid" |
| Secret length is not 64 | The beginning or end was lost while copying. The hub returns 204 even for an invalid signature; this is the only symptom |
| Events arrive with the wrong environment | If `VITALWATCH_ENV` is empty, the ASP.NET Core environment is used; if `ASPNETCORE_ENVIRONMENT` is an unrecognized value (`QA`), the hub rejects it |
| No `job_failed` events | `ClearProviders()` is called after `AddVitalWatch()`, or the `BackgroundService` catches the exception itself |
| No `query_count` in `slow_request` | `UseVitalWatch()` was not added, or the queries run before/after the request (in the background) |
| `[token]` in the stack trace | Plain CamelCase names (`InvalidOperationException`, `HandleNonSuccessAndDebuggerNotification`) are not masked. A name of 24+ characters that contains a digit, `_`/`-` or two adjacent uppercase letters (`IOExceptionHandlerMiddleware1`) matches the token pattern. The pattern is shared by all SDKs and the hub; the hub re-masks stack traces with the same rule |
| The last error was lost on shutdown | The queue is drained for at most 3 s on shutdown; if the process is killed with `kill -9`, the queue is lost |

To disable temporarily: set `VITALWATCH_ENABLED=false` and restart the application.

---

## Development

```bash
dotnet test                      # net8.0 + net10.0
dotnet test -f net10.0           # if only the .NET 10 runtime is installed
dotnet pack src/VitalWatch.AspNetCore -c Release -o artifacts
```

Tests run against a fake hub (Kestrel, `127.0.0.1:0`) that implements the hub's signature
verification exactly; "got a 204" is not enough — they verify that the signature and the
8 KB / 8 event limits hold. Masking tests read the shared vector file produced by the hub
(`tests/VitalWatch.AspNetCore.Tests/Fixtures/mask-vectors.json`).

Release: bump `<Version>`, push a tag such as `v0.1.0`; the workflow uploads to NuGet via
trusted publishing.

## License

MIT

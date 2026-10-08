# Decisions

Choices the specification did not dictate, with the reasoning. IDs are never reused.

### D-001 - .NET 10 with the SDK and runtime already installed

The owner's machine has .NET SDK 10.0.400 with runtime 10.0.11. .NET 10 is the current LTS and
every chosen library supports it, so no install step is needed (`RULE-INSTALLS`). The SDK is
pinned in `global.json`; ASP.NET Core and EF Core packages are pinned to 10.0.11 to match the
runtime rather than the newest patch on NuGet, so the local run, CI (which installs the same
SDK) and the runtime image all execute the same framework (spec 6.2).

### D-002 - Rate providers behind an interface, routed by currency in code

`USD` and `EUR` come from the ECB, `RUB` from the Bank of Russia, each an `IRateProvider` with
its own cache and freshness window. There is no provider setting: which official source prices
which currency is a fact of the domain, not configuration, and a paid market-data provider can
later replace one implementation without touching the engine (`L-1`).

### D-003 - Short-lived JWT, kept in memory

A Mini App can re-validate `initData` at any time, so a 60-minute token held in the page's
memory costs nothing in usability and shrinks the damage of a stolen token. The page re-signs in
silently once when the token expires, and never writes the token to any storage (`INV-18`).

### D-004 - The front end is plain HTML, CSS and JavaScript

The page is a thin client over the HTTP API, which carries every rule. A framework or Blazor
WebAssembly would add a build step and, for Blazor, a multi-megabyte download inside Telegram on
a phone, for a four-tab form. Plain files need no toolchain, load instantly, and are easy to
scan statically for what they must never do (`INV-18`).

### D-005 - No admin setting until the admin panel exists

The admin panel is `PHASE-6`, which is deferred. Requiring an admin password hash from the first
phase would make every installation invent a value for a feature that does not exist. The
setting arrives with that phase, and joins demo mode's refusal list then (`D-013`).

### D-006 - Two source projects: a pure domain and the host

`Exchange.Domain` holds what can be wrong without any I/O - directions, detail field rules, the
rate engine, quantization, the status machine, order limits - and references nothing but the
BCL. `Exchange.Api` holds everything that talks to the outside. Most hard classes of
`RULE-TEST-FIRST` live in the domain project, whose tests need no Docker and run in seconds. A
third "infrastructure" project would only move files.

### D-007 - Money in requests is a JSON string; a JSON number is refused

The page always sends money as strings. `System.Text.Json` could read a number into `decimal`
exactly, but accepting both means two code paths and a contract where `1e2` is an amount. A
money field holding a JSON number is refused with `invalid_request` (spec 1.10).

### D-008 - Migrations run by a command; demo mode applies them at startup

Applying migrations on every start is a hazard once more than one instance runs, so outside demo
mode they are applied by `Exchange.Api migrate`. Demo mode always starts on an empty database in
one process, so it applies them itself at startup, and the container needs one command.

### D-009 - xUnit v3 on Microsoft.Testing.Platform

The .NET 10 SDK runs `xunit.v3` through Microsoft.Testing.Platform when `global.json` says so,
and the VSTest packages (`Microsoft.NET.Test.Sdk`, `xunit.runner.visualstudio`) must not be
added beside it. `--nologo` must not be passed to `dotnet test` there: the run reports zero
tests and exits 5, which would make the stop gate a permanent false red (spec 6.2).

### D-010 - `DATABASE_URL` is a URI

A single URI (`postgresql://user:password@host:port/database`) is the common form for container
platforms and compose files, and one variable is easier to keep secret than five. One function
converts it to an Npgsql connection string; the scheme must be `postgresql` or `postgres`,
anything else is refused at startup, and the conversion never sets `Include Error Detail`
(spec 4).

### D-011 - Half-down rounding is written by hand, and rate precision is the column's

The office rounds receive amounts to nearest with ties toward zero (`NR-3`). `System.Decimal`
has no such mode: `MidpointRounding.ToZero` is directed rounding (it truncates every value, not
only ties). Using it would make every receive amount up to a cent low, silently - the project's
second-worst failure. So `Quantize` implements half-down itself, `ToZero` is banned from money
code (`INV-14`), and its tests use a hand-computed table with exact ties and values on either
side of them. `System.Decimal` holds 28-29 significant digits, so intermediate rates are
quantized to the stored `NUMERIC(24,12)` scale, which it holds exactly for every realistic rate;
the rate-engine matrix tests pin the outputs for every direction.

### D-012 - XML with a DTD is refused outright

Both rate feeds are plain XML without a DTD. `XmlReader` with `DtdProcessing.Prohibit` and no
resolver refuses every DTD, which closes entity expansion and external entities without
depending on a parser version or on a hand-written allow-list.

### D-013 - Demo mode refuses the production secrets that exist

Demo mode refuses to start beside `BOT_TOKEN`, `BOT_WEBHOOK_SECRET`, `JWT_SECRET` and
`PII_ENCRYPTION_KEY` (spec 1.9). When `PHASE-6` brings an admin secret, it joins that list in
the same change.

### D-014 - Invariant globalization

`InvariantGlobalization` is on for every project. Money, rates and dates must be parsed and
formatted culture-free (spec 1.3, 1.4a), and with globalization invariant no code path can pick
up the machine's culture by accident - a Russian-locale machine would otherwise turn `1.5` into
`1,5`. Nothing needs a culture: the Russian texts are data (spec 1.7), and the Bank of Russia's
`windows-1251` is a code page, which `CodePagesEncodingProvider` serves without ICU.

### D-015 - Test projects do not document their members

`GenerateDocumentationFile` stays on everywhere because `IDE0005` (unnecessary usings) only runs
on build with it. Test members would then each need an XML comment (`CS1591`); test names are
written as sentences and are their own documentation, so the test projects suppress `CS1591`
alone, with the reason beside the suppression (`RULE-BUILD-HYGIENE`).

### D-016 - `DATABASE_URL` accepts one query parameter, `sslmode`

Spec 1.9 has demo mode refuse a password in the URL's query, and a production database may need
TLS. The converter therefore understands exactly one parameter, `sslmode` (`disable`, `prefer`,
`require`, `verify-ca`, `verify-full`), given at most once, and refuses any other - `password`
and `passfile` included - in every environment, not only in demo. For the same reason demo mode
refuses `PGPASSFILE` beside `PGPASSWORD`; a default `~/.pgpass` cannot be seen from the
settings, and the demo's database trusts its network and needs none. A setting the app does not understand is a
setting it cannot be trusted to apply.

### D-017 - `.env` is read in development only, below the process environment

Spec 1.11 reads `.env` in development. `APP_ENV` is read once, from the process environment
only, and that one value decides ASP.NET Core's environment, whether `.env` is read and the
settings rules; the loader never reads it from configuration, and a `.env` that sets it is
refused, so neither a `.env` nor a command-line argument can switch an installation's mode. It
takes exactly `development`, `test`, `production` or `demo` (`Enum.TryParse` would accept
`test,production` as a bit-or). `.env` is looked for in the working directory, and its pairs are
the lowest-priority configuration source: a variable set in the process wins. A malformed line
stops the start with its line number only, since the line may hold a secret. The test process
runs with `APP_ENV=test`; tests that need another value switch it inside one collection that
xUnit runs alone.

### D-018 - Settings are validated after the host is built, and the listening address is an IP

This replaces the Options pattern with `ValidateOnStart` that spec 6 first named (the spec row is
amended). The settings are not independent sections to bind: demo mode changes which of them are
required and which are refused, the order limits are checked against each other, and every
problem has to be reported at once, by name and without its value. A binder plus per-option
validators would split those rules across types and report them one type at a time. A single
loader keeps them in one place and testable as a pure function.

`SettingsLoader` runs as the factory of the `AppSettings` singleton, resolved right after
`Build()` and before the app serves: it then reads the final configuration, the one tests also
override, and every problem is reported in one exception. Kestrel takes its address from the same
object, so `SERVE_HOST` must be an IP address or `localhost`; a host name would need a DNS lookup
at start and is refused.

### D-019 - The request line, and what is quiet

Each request is logged once by Serilog's request logging, as `HTTP <method> <route> responded
<status> in <ms> ms`, with exactly the properties `method`, `route`, `status` and `duration_ms`
(spec 4's names); the route is the endpoint's template (`/api/orders/{order_id}`), and the raw
path is not logged at all: ASP.NET Core's request scope would add `RequestPath` (and its own
`RequestId`) to every line of a request, so the logging setup drops both properties. A request line for a failed request (5xx, or one that ended in an
exception) is a `WARNING`: the top-level handler has already logged the failure at `ERROR`, and a
second `ERROR` would alert twice. For the same reason the handler, when the response has already
started and the 500 body can no longer be sent, aborts the connection instead of rethrowing into
the server, which would log the failure again. The request middleware is given the app's
logger explicitly, since it otherwise writes to Serilog's static logger, outside the app's outputs
and alerts. The whole `Microsoft.AspNetCore` category is raised to `WARNING`, a superset of the
`Hosting.Diagnostics` category spec 4 names: its other `INFO` lines (endpoint selected, result
written) repeat the request line and can carry paths.

### D-020 - A failed start: one FATAL line, exit code 1

The bootstrap logger exists before anything else, so a failure to start is logged with the same
redaction as everything else. Refused settings become one `FATAL` line listing every problem by
variable name; any other exception is logged `FATAL` with its stack. The process then exits with
code 1, rather than rethrowing into the runtime's own crash output, which would print the
exception a second time outside the logger. Because the entry point catches the exception, the
in-memory test host cannot observe it; `StartupFailureTests` runs the real app as a process and
checks the exit code and the output instead. This FATAL line is the one place that writes through
Serilog's static API rather than an `ILogger` with `[LoggerMessage]`: it runs before, or after the
end of, the host that provides one.

### D-021 - Alerts: behind redaction, queued, delivered off the request path

The alert sink is an output behind the redacting sink, so an alert can never carry what a log line
could not. It queues at most 100 alerts (`AlertSink.QueueCapacity`) and drops new ones when full,
recording the drop in Serilog's self-log, so a storm of errors cannot block a request or grow
memory. The cooldown check and its record happen under one lock, so concurrent identical errors
still raise one alert. If one output of the redacting sink throws, the others still receive the
event and the failure goes to the self-log. Delivery runs in a background service; until the Telegram client exists (`PHASE-3`) it
writes an `INFO` line naming the alert's fingerprint. A failed delivery goes to the self-log
(stderr) only: logging it would raise another alert. In demo mode the sink is not attached.

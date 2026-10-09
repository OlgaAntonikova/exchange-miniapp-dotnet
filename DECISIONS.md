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
settings, and the demo's database trusts its network and needs none. A setting the app does not
understand is a setting it cannot be trusted to apply.

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

This replaces the Options pattern with `ValidateOnStart` that spec 6 first named (the spec row
is amended, and the owner approved the amendment on 2026-10-08). The settings are not
independent sections to bind: demo mode changes which of them are required and which are
refused, the order limits are checked against each other, and every problem has to be reported
at once, by name and without its value. A binder plus per-option validators would split those
rules across types and report them one type at a time. A single loader keeps them in one place
and testable as a pure function.

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
`RequestId`) to every line of a request, so the logging setup drops both properties. A request
line for a failed request (5xx, or one that ended in an exception) is a `WARNING`: the
top-level handler has already logged the failure at `ERROR`, and a second `ERROR` would alert
twice. For the same reason the handler, when the response has already started and the 500 body
can no longer be sent, aborts the connection instead of rethrowing into the server, which would
log the failure again. The request middleware is given the app's logger explicitly, since it
otherwise writes to Serilog's static logger, outside the app's outputs and alerts. The whole
`Microsoft.AspNetCore` category is raised to `WARNING`, a superset of the `Hosting.Diagnostics`
category spec 4 names: its other `INFO` lines (endpoint selected, result written) repeat the
request line and can carry paths.

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

The alert sink is an output behind the redacting sink, so an alert can never carry what a log
line could not. It queues at most 100 alerts (`AlertSink.QueueCapacity`) and drops new ones
when full, recording the drop in Serilog's self-log, so a storm of errors cannot block a
request or grow memory. The cooldown check and its record happen under one lock, so concurrent
identical errors still raise one alert. If one output of the redacting sink throws, the others
still receive the event and the failure goes to the self-log. Delivery runs in a background
service; until the Telegram client exists (`PHASE-3`) it writes an `INFO` line naming the
alert's fingerprint. A failed delivery goes to the self-log (stderr) only: logging it would
raise another alert. In demo mode the sink is not attached.

### D-022 - Generated migrations are reviewed, not restyled

`dotnet ef migrations add` writes code in its own style (block namespaces, constant arrays), which
the project's analyzers refuse. The migrations folder is marked `generated_code` in
`.editorconfig`, so analyzers and the formatter skip it: the next generated migration then has
the same shape as this one, and the review reads the SQL (`dotnet ef migrations script`), which
is what runs. Hand edits are limited to what generation cannot do - here, the seed of spec 1.2.

### D-023 - How the schema holds PII and its seed

PII columns are `bytea` holding the cipher's token (`PiiCipher`), converted by an EF value
converter bound to its column name. The converter captures the context's cipher, and EF caches a
model per context type, so a model cache key that includes the cipher makes each cipher (one per
process; one per test where tests use their own keys) get its own model rather than another
process's key. The seed is plain SQL in the migration with no explicit ids, so the identity
sequences start after it and the first row added later never collides. Demo mode applies the
migrations from a hosted service registered first, which runs when the host starts - also in
the in-memory test host, where the entry point stops at `Build()`. Tests run against one
PostgreSQL container with trust authentication, as the demo's will have, and create a database per
test. Its image, `postgres:16.15-alpine`, is the exact tag spec 6 asks for; it is fixed here, in
`PHASE-1`, when the first container arrived (spec 6 had expected it in `PHASE-0`), and the demo's
compose file will use the same tag.

### D-024 - A first migration logs no ERROR

On a new database EF Core 10 probes the migrations history with a query that fails, and logs the
failed command at `ERROR` before it creates the table. Every `ERROR` alerts the operator
(spec 4), so a normal first `migrate` would raise an alert. Creating the history table first did
not stop the probe. The migration therefore runs on a context of its own whose EF failed-command
event is logged at `DEBUG`; nothing else is quietened, and the app's own contexts keep the
default. A migration that really fails still throws, and the caller logs it - the `migrate`
command and a demo start both end in a `FATAL` line and a non-zero exit (`D-020`), which
`MigrateCommandTests` checks against an unreachable server.

### D-025 - The vocabulary of the status history

Spec 1.5 records "from, to, actor, timestamp" for every status change. The actors are `client`
(a cancellation by the client), `operator` (a button in the operator chat), `admin` (the admin
panel of `PHASE-6`) and `system` (the order's creation, the one row whose `from_status` is NULL).
They are stored in lower case, like the transfer times and unlike the statuses and codes of the
API, because they never appear in the API: they are internal labels. `actor_telegram_id` records
which person pressed a button, since several operators may share the chat
(`OPERATOR_TELEGRAM_IDS`). The set is a database CHECK, so changing it is a migration.

### D-026 - What the reference endpoints promise beyond the spec

`GET /api/directions` lists the pairs in a fixed order: give codes in the order of spec 1.1
(`RUB_WIRE`, `USD_CASH`, `EUR_CASH`, `USDT`), and for each the receive codes in the same way;
field names within a list are sorted. It is built once from the domain table the API enforces,
so the form never keeps a copy of its own. Reference lists are ordered by id, which is the
seed's order.

### D-027 - A same-base direction is refused before its fields

When a quote or order request names a same-base direction and also has field problems, the
same-base refusal (`invalid_direction`) is reported alone: the fields of a direction that cannot
exist are not meaningful. `Directions.Check` returns it before looking at any field.

### D-028 - What the initData validator refuses beyond the signature and the age

The spec fixes the HMAC check (`INV-1`) and the age (`INV-2`, with a 60-second skew). Around
them: the data-check-string is every field except `hash` - `signature` included - sorted by key
in byte order and joined by line feeds, as Telegram's documentation describes the HMAC method
(the `PHASE-2` gate, with a real `initData`, is the proof); the query is parsed strictly (every
segment `key=value` with a key, every escape `%XX`, no raw non-ASCII character (Telegram
percent-encodes it), `+` as a space, the decoded bytes valid UTF-8); a field sent twice is
refused, since which copy was signed would be ambiguous; input over 8192 characters is refused
before parsing (a generous margin over a user object and a few short fields); the received hash
must be the exact lower-case hex, compared in constant time; `auth_date` is plain ASCII digits;
`user.id` must be a positive JSON integer and `first_name` a string. Every refusal carries a
short reason code and nothing of the input; parser exceptions, which can quote it, are not
chained.

### D-029 - The access token, the sign-in body, and the one 401

The spec fixes HS256 and `JWT_TTL_SECONDS`. This decision fixes the claims: exactly `sub`,
`iat`, `exp` and `aud`, where `sub` is the Telegram user id as a decimal string; `aud` is the
constant `exchange-miniapp-dotnet`; there is no `iss` (this app is the only issuer and the key is
its own) and no `nbf`. Validation accepts HS256 only, requires a signature, `exp` and the
audience, and allows no clock skew; the lifetime check reads the app's `TimeProvider`, not the
system clock, so expiry is tested at its exact second. A valid token whose subject is not plain
digits, or names no user, is refused too.

`POST /api/auth/telegram` reads its body itself, up to twice `MaxInitDataChars` bytes: anything that
is not a JSON object with an `init_data` string - an empty, oversized or malformed body included -
is the 401 of `INV-21`, like a bad `initData`, rather than a framework 400 with a body the spec does
not list. Other properties (`user_id`, `telegram_id`) are ignored (`RULE-SERVER-AUTHORITY`). In demo
mode there is no bot token, so this route always refuses (spec 1.9). A sign-in creates the user or
refreshes `username` and `first_name` in one `INSERT ... ON CONFLICT` statement, keeping the
loyalty points.

Every refusal - by the bearer handler's challenge or by an endpoint - writes the same body and
`WWW-Authenticate: Bearer`, and logs one `WARNING` with a short `Reason` (a code, or the token
library's exception type name) and nothing of the token or the input.

### D-030 - What the rate engine refuses, and what it leaves to the API

`RateEngine.Quote` is the formula of spec 1.3 and nothing else: it takes mids, markups and the
discount as given and does no I/O. Besides a same-base direction (`INV-6`) it refuses, with a
reason code, inputs no quote can come from: a non-positive amount, a negative markup component or
discount, an applied markup of 100 % or more (nothing would be paid out), a missing or
non-positive mid, a base rate beyond what a decimal can hold at the rate scale
(`rate_out_of_range`), and an effective rate that rounds to zero at the rate scale. The total and
the applied markup are quantized to the percentage scale before they enter the formula - spec 1.3
quantizes only the rates - so the applied markup a quote reports is the one its effective rate was
computed from (`INV-4`). A receive amount that quantizes to zero is
not refused here: it is a valid result of the formula, and turning it into the 422
`amount_too_small` of section 1.10 belongs to the endpoint, as do the order limits.

### D-031 - How the two rate sources are read

Both providers share one fetch: a GET that only a 200 answer passes, with every failure turned
into a short reason code that names no URL and no body (`http_<status>`, `transport_<type>`,
`timeout`); the transport's exception is not chained, since its message quotes the URL. A
cancellation the caller asked for is passed on as a cancellation, not a failed fetch. XML is
read with `DtdProcessing.Prohibit` and no resolver (`D-012`), so any document with a DTD is
`malformed_xml`. Numbers are ASCII digits with an optional fraction after the source's own
decimal mark - a point for the ECB, a comma for the Bank of Russia, and never the other - and
must be positive; the Bank of Russia's `Nominal` must be a whole number. Dates are parsed with
an exact format. The Bank of Russia body is decoded as windows-1251 by the provider itself,
whatever the document declares, so the parser never picks an encoding; while only ASCII fields
are read, that decoding cannot be observed, and no test claims to guard it. A snapshot carries
`ValidUntil`, the start of the day after the last day its effective date may be used, at the
source's fixed offset (UTC+1 for the ECB, which allows no lead, UTC+3 for the Bank of Russia),
so a cached snapshot is refused once its window closes, not only when it is fetched.

### D-032 - The rate cache, the freshness guard and the routes

Each source has one `RateService`. It fetches at most once per cache period whether the last
fetch worked or not, so a dead source is not hit by every request; a caller that gives up
mid-fetch leaves no attempt behind. The last good snapshot is served while the time since it
was fetched is at most the source's maximum age - exactly the maximum age is still fresh, which
settles the one instant where spec 1.4's "younger than" and `RULE-RATE-FRESHNESS`'s "older
than" differ - and the moment is before its `ValidUntil` (`D-031`); otherwise the answer is
`RatesUnavailableException` with a reason code and a `WARNING`, never a number. A failed fetch
is logged at `ERROR` with the provider, the reason and the duration. To give each source its
own alert on `RATE_ALERT_COOLDOWN_SECONDS`, the alert sink reads two optional event properties:
`AlertKey`, appended to the fingerprint, and `AlertCooldownSeconds`, a positive whole number of
seconds that replaces the default cooldown; the rate service sets both for its failures.
`RateSources` routes USD and EUR to the ECB and RUB to the Bank of Russia, asks each source
once per quote for only the currencies the direction needs, names the providers it used joined
by `+` in alphabetical order, and takes the oldest snapshot time. Each source's `HttpClient`
has the `RATE_FETCH_TIMEOUT_SECONDS` timeout and a 1 MiB response limit.

### D-033 - How POST /api/rate reads a request, and where the server's texts come from

The quote body is read up to 16 KB and parsed with duplicate properties refused, since which
copy counts would be ambiguous; an unreadable, oversized, non-object or duplicated body is the
422 `invalid_request` naming `body`. Property names are matched exactly in snake_case: a
case-insensitive match is not used, so `Give` is a property outside the contract and is ignored
like `user_id`. A reference id sent as JSON `null` counts as absent, as if the property were
not there - the form sends `null` for a field it cleared - while any other non-integer is
refused. The request is checked in stages, and each stage names every field it faults before
the next one runs: first the values (`give`, `receive`, `amount_give`, then the reference ids
in the order of section 1.10), then the direction (a same-base pair, then its missing fields
before the fields it does not use, each group sorted), then the reference rows. A reference id
must name a row that exists, is active and fits its side (a sender bank `WIRE`/`RUB`/`RU`, an
ATM bank `ATM`/`EUR`/`ME`, a city in `ME`); every row that does not is listed. The order limits
of section 1.5 are not applied to a quote: `amount_out_of_range` is an order's refusal, and a
quote of any valid amount is shown. Once the request is valid, an engine refusal (markups
reaching 100 %, a broken mid) is the server's fault and is the 500 of section 1.10. The pricing
path is one class, `Pricer`, which orders will reuse.

The server's texts come from `wwwroot/locale/ru.json`, copied beside the assembly at build and
publish, so a host started from any directory, the `migrate` command included, reads the same
file; it is loaded at startup, and a missing key fails the start. A text's `{name}` placeholders
must match the values given exactly, or the call fails. The file holds only the keys the server
uses so far; the page's keys arrive with the front end in `PHASE-5`. Each rate source's client and
cache service are keyed singletons, so the container disposes them with the app.

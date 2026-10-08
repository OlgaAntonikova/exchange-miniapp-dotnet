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

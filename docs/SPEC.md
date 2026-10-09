# Exchange Mini App - Specification

The single source of truth for this project. Built under the dev-factory process; every
referenceable item carries an identifier (`RULE-*`, `INV-n`, `PHASE-n`, `L-n`, `Q-n`) and is
cited by it, never by position.

---

## 1. What we build

A Telegram Mini App for a currency-exchange office operating in Montenegro, built on ASP.NET
Core. A client opens the app inside Telegram, picks what they give and what they receive, sees a
live quote with all markups applied, and places an **exchange request** (order). The app does
**not** move money: an operator receives the order in a Telegram chat and completes the deal by
hand, changing its status from that chat.

The product is a portfolio piece. It ends as a demo anyone can run with `docker compose up`
and no secret (section 1.9), in a public repository the owner publishes (`PHASE-7`).

### 1.1 Exchange directions

Give (`from`): `RUB_WIRE`, `USD_CASH`, `EUR_CASH`, `USDT`.
Receive (`to`): `EUR_CASH`, `EUR_ATM`, `EUR_WIRE`, `EUR_INVOICE`, `USDT`.

| Side | Code | Required extra fields |
|---|---|---|
| from | `RUB_WIRE` | sender bank (banks with channel `WIRE`, currency `RUB`, country `RU`) |
| from | `USDT` | network; transfer time (`now` or `at_meeting`) |
| from | `USD_CASH`, `EUR_CASH` | none |
| to | `EUR_CASH` | city |
| to | `EUR_ATM` | ATM bank (channel `ATM`, currency `EUR`, country `ME`), IBAN, recipient name |
| to | `EUR_WIRE`, `EUR_INVOICE` | IBAN, recipient name; payment purpose optional |
| to | `USDT` | network, wallet address |

A direction whose `from` and `to` resolve to the same base currency is rejected (`INV-6`).

Detail field rules (each a named constant, each with negative tests):

| Field | Rule |
|---|---|
| IBAN | whitespace removed, upper-cased; 15-34 characters; two letters, two digits, then letters and digits; the ISO 7064 MOD 97-10 check holds: the first four characters moved to the end, each letter replaced by its number (A=10 ... Z=35), the resulting integer mod 97 equals 1 |
| recipient name, payment purpose | whitespace runs collapsed to one space; not empty; at most 140 characters; no control characters |
| wallet address | trimmed; 20-128 characters of `A-Z a-z 0-9 _ + / = : -`; deliberately loose, networks not told apart (`NR-6`) |

### 1.2 Reference data

Stored in the database and seeded by migration with the values below (their currency is
`NR-2`).

| Table | Seed |
|---|---|
| `cities` (country `ME`, `markup_percent`) | Podgorica 0.0, Budva 0.5, Bar 0.5, Tivat 0.7, Kotor 0.7, Herceg Novi 1.0 |
| `banks` (`channel`, `currency`, `country`, `markup_percent`) | SBER WIRE/RUB/RU 0.0, TINKOFF WIRE/RUB/RU 0.5, LOVCEN ATM/EUR/ME 0.0, CKB ATM/EUR/ME 0.0, ERSTE ATM/EUR/ME 0.5 |
| `usdt_networks` (`fee_percent`) | TRC20 0.3, TON 0.4, ERC20 1.0 |

Every row has `is_active`; inactive rows are invisible to clients and rejected in requests.
Editing them in an admin panel is `PHASE-6`, which is deferred; until then they change by
migration only.

### 1.3 Rate engine

- Base currencies: every `EUR_*` normalises to `EUR`, `RUB_WIRE` to `RUB`, `USD_CASH` to `USD`,
  and `USDT` to `USD` (the peg is a domain claim, `NR-1`).
- `base_rate = mid(to) / mid(from)`, where `mid` is units per one USD from the provider that
  serves the currency: `USD` and `EUR` from the ECB (section 1.4), `RUB` from the Bank of
  Russia (section 1.4a).
- `total_markup_pct = city + bank_give + bank_receive + network_give + network_receive`, each
  taken from the reference tables, zero when not applicable.
- `applied_markup_pct = max(total_markup_pct - loyalty_discount_pct, 0)` - the office never
  quotes better than mid-market.
  `effective_rate = base_rate * (1 - applied_markup_pct / 100)`. The loyalty discount is 0 until
  `PHASE-6` (section 1.6).
- `amount_receive = Quantize(amount_give * effective_rate)` to 2 decimal places, rounding **to
  nearest with ties toward zero** (in the client's disfavour by at most half a cent, `NR-3`).
  .NET has no such mode: `MidpointRounding.ToZero` truncates every value (1.239 becomes 1.23),
  so it is **forbidden** in money code, and `Quantize` is written by hand in `Exchange.Domain`.
  Its tests use a hand-computed table that includes exact ties (`x.xx5`) and values just above
  and just below a tie (`D-011`).
- All arithmetic is `System.Decimal`. A `double` or `float` never touches money
  (`RULE-MONEY-DECIMAL`). Column scales: `NUMERIC(18,2)` for amounts, `NUMERIC(24,12)` for
  rates, `NUMERIC(7,4)` for percentages; JSON strings carry the column's scale (a markup reads
  `"0.5000"`). Intermediate rates are quantized to the rate scale before they are stored or
  multiplied into an amount (`D-011`).

### 1.4 Rate provider - European Central Bank for USD and EUR

The European Central Bank's euro foreign exchange reference rates: an official, free source
whose reuse is permitted with the source named, which the README does. It sits behind the
`IRateProvider` interface.

- The source is `eurofxref-daily.xml`: units of each currency per one `EUR`, for the date in its
  `time` attribute. `mid(USD) = 1` and `mid(EUR) = 1 / (USD per EUR)`, so the engine keeps
  working in units per one USD (section 1.3).
- `ECB_RATES_URL` is a plain setting with the default
  `https://www.ecb.europa.eu/stats/eurofxref/eurofxref-daily.xml`, not a secret; it is still
  never logged (its key ends in `URL`).
- The rates are reference rates, published for information at about 16:00 Frankfurt time on
  TARGET business days; they are not market or transaction rates (`L-1`, `NR-7`).
- Responses are cached for `ECB_RATE_CACHE_SECONDS` (default 3600). A snapshot is fresh while
  the last successful fetch is younger than `ECB_RATE_MAX_AGE_SECONDS` (default 129600, 36 h) and
  the date it reports, compared with today's date in Frankfurt, is not in the future and at most
  `ECB_RATE_MAX_EFFECTIVE_AGE_DAYS` (default 5) behind. Frankfurt's date is taken at a fixed
  UTC+1: the hour summer time moves never crosses midnight near a 16:00 publication, and no
  time-zone database is needed.
- XML is read with `XmlReaderSettings { DtdProcessing = Prohibit, XmlResolver = null }`: a
  document with a DTD is refused, so entity-expansion and external-entity attacks cannot start
  (`D-012`).
- Every fetch is logged with its outcome and duration; every failure is logged at `ERROR` and
  alerts the operator chat (at most once per `RATE_ALERT_COOLDOWN_SECONDS`).
- On failure the last good snapshot is served while it is fresh. After that the API **refuses to
  quote** every direction that needs `USD` or `EUR` (HTTP 503, section 1.10) and refuses to
  create orders for them (`RULE-RATE-FRESHNESS`). A stale or invented rate is never shown.

### 1.4a Rate provider - Bank of Russia for RUB

The Bank of Russia's official daily rate is the `RUB` source, a second `IRateProvider`.
Currencies are routed to providers in code - `USD` and `EUR` to the ECB, `RUB` to the Bank of
Russia - and `RUB` per USD is the official USD rate (`Value / Nominal` of `XML_daily.asp`).

- `CBR_RATES_URL` is a plain setting with the default `https://www.cbr.ru/scripts/XML_daily.asp`,
  not a secret, never logged.
- The rate is official, not a market mid, and is set for a calendar date (`NR-5`, `L-5`).
- The response is `windows-1251` (decoded through `CodePagesEncodingProvider`, which ships with
  .NET) and uses a decimal comma, parsed with an explicit culture-free rule, never the machine's
  culture.
- Responses are cached for `CBR_RATE_CACHE_SECONDS` (default 3600). A snapshot is fresh while the
  last successful fetch is younger than `CBR_RATE_MAX_AGE_SECONDS` (default 129600, 36 h) and
  the effective date it reports, compared with today's date in Moscow (fixed UTC+3), is at most
  `CBR_RATE_MAX_EFFECTIVE_LEAD_DAYS` (default 1) ahead and at most
  `CBR_RATE_MAX_EFFECTIVE_AGE_DAYS` (default 15) behind.
- XML safety, logging, alerting and refusal follow section 1.4: a direction that needs `RUB` is
  refused with 503 while the `RUB` snapshot is not fresh; directions that do not need it are
  unaffected.

### 1.5 Orders

- `POST /api/orders`: the server validates the direction and its extra fields, **recomputes the
  rate itself**, and stores the rate snapshot (base rate, every markup component, effective rate,
  the providers used and the oldest of their snapshot timestamps) together with all detail
  fields. A client-sent `amount_receive`, rate or user id is not part of the request contract:
  such properties are ignored and change nothing (`RULE-SERVER-AUTHORITY`, section 1.10).
- The amount must lie within the give-currency's limits, inclusive at both ends, settings
  `<CODE>_ORDER_MIN` / `<CODE>_ORDER_MAX`, each in its own currency: `USD_CASH`, `EUR_CASH`,
  `USDT` 100 - 10 000; `RUB_WIRE` 10 000 - 1 000 000 (the owner's values).
- Statuses: `PENDING -> CONFIRMED -> PROCESSING -> COMPLETED`; `CANCELLED` from `PENDING`,
  `CONFIRMED` or `PROCESSING`. Any other transition is rejected (`INV-9`).
- The client may cancel only their own `PENDING` order. All other transitions are operator-only.
- Every status change goes through **one** transition function, under a row lock
  (`SELECT ... FOR UPDATE`), and appends to `order_status_history` (from, to, actor, timestamp).
  Entering `COMPLETED` or `CANCELLED` sets `closed_at`.
- After creation the operator chat receives a message (order id, client handle, amounts,
  direction, rate, details) with inline buttons for the allowed next statuses. The callback
  handler accepts presses only from Telegram user ids in `OPERATOR_TELEGRAM_IDS`.
- The client receives a bot message on every status change.
- A notification failure never fails the order: it is logged at `ERROR` and retried
  (3 attempts, exponential backoff) from a background queue, off the request path.
- In order responses the IBAN and the wallet address are masked: the first 4 and last 4
  characters around an ellipsis (`ME25...8951`), or the ellipsis alone for 8 characters or
  fewer. The recipient name and payment purpose are the client's own text and are returned as
  stored.
- Client PII is kept until 90 days after `closed_at`, then purged; amounts, rates and statuses
  are kept. The schema carries `closed_at` and `pii_purged_at`; the purge job itself is not
  built (`L-4`).

### 1.6 Loyalty

Deferred with `PHASE-6`. When built:

- +1 point per `COMPLETED` order (not on creation), capped at 10.
- Discount tiers: at least 6 points gives 1 %, at least 8 gives 2 %, 10 gives 3 %; subtracted
  from the total markup as section 1.3 shows.

Until then `loyalty_points` exists on the user, stays 0, and is shown as stored; no discount is
applied.

### 1.7 Client UI (Telegram WebApp)

Plain HTML, CSS and JavaScript in `wwwroot` - no framework, no build step (`D-004`): `index.html`,
`app.css`, `app.js` and `locale/ru.json`.

Dark theme following Telegram theme parameters, mobile-first, tabs **Home / Exchange /
Privileges / Profile**.

- Exchange: amount, give selector, dynamic extra fields (read from `GET /api/directions`),
  read-only receive amount, rate with a markup breakdown, "Place order" disabled until the form
  is valid, confirmation modal, success screen. The quote refreshes on input (debounced 400 ms)
  and every 60 s while the tab is open, with a visible "updated" indicator and a clear message
  when quoting is unavailable.
- Profile: name, order count, points, current discount, semicircle progress to 10 points, hint to
  the next tier, order history with statuses and a cancel action on `PENDING` orders.
- The page keeps the access token in memory only (`D-003`), builds the DOM with `textContent`
  only, keeps money as strings, and loads nothing from outside `telegram.org` (`INV-18`).
- All user-facing text is Russian and lives in one locale file shipped as data
  (`RULE-ENGLISH-ONLY` exception): `wwwroot/locale/ru.json`. The server reads the **same file**
  for every text it produces (bot messages, the 503 `message`) and fills its `{name}`
  placeholders with one small named-placeholder formatter; a missing key or placeholder is a
  startup or test failure, never an empty string.
- `wwwroot` is served at `/static`, and `GET /` returns `index.html`; responses carry
  `Cache-Control: no-cache` so a phone revalidates after an update.

### 1.8 Admin panel

Deferred with `PHASE-6`. When built: Razor Pages under `/admin` with one admin account, calling
the same transition function as the bot. No admin setting or secret exists until then
(`D-005`).

### 1.9 Demo mode

So that anyone can try the app without Telegram, a bot or any secret, `APP_ENV=demo` turns the
app into a self-contained demo, run by `docker compose up`:

- **Settings.** Of the required settings only `DATABASE_URL` is read, and it must carry no
  password: the compose file points it at a PostgreSQL that trusts connections on the compose
  network and publishes no port. `OPERATOR_CHAT_ID` and `OPERATOR_TELEGRAM_IDS` are not needed.
  The JWT key and the PII key are generated at random on each start and never stored.
- **Refusals (`INV-16`).** Demo mode refuses to start when any production secret is set -
  `BOT_TOKEN`, `BOT_WEBHOOK_SECRET`, `JWT_SECRET`, `PII_ENCRYPTION_KEY` - or when `DATABASE_URL`
  carries a password (in its user part, in its query, or in `PGPASSWORD`), so a real environment
  cannot be switched into demo by a stray variable, nor a demo pointed at a real database.
- **Throwaway data.** The demo database keeps its data on a `tmpfs`, so every `docker compose up`
  starts empty; nothing encrypted under an earlier start's key survives to fail decryption
  (`INV-13`). Demo mode applies the migrations at startup (`D-008`). One process: the keys live
  in it.
- **No Telegram.** Nothing is sent to Telegram: neither operator notifications, client messages
  nor alerts. Each message is replaced by an `INFO` record naming only its kind and the order id;
  the message text, which carries client PII, is never logged (`RULE-SECRETS`). An alert is
  dropped, since the `ERROR` record it would carry is already in the log. The webhook route is
  not mapped, the bot poller refuses to start, and the page uses the browser's own dialogs.
- **Sign-in.** `POST /api/auth/demo` signs the browser in as one fixed demo user and returns a
  token, as `POST /api/auth/telegram` does. It exists **only** in demo mode; in every other
  environment the route is absent (404) and creates nothing (`INV-15`). The page, opened without
  Telegram's `initData`, calls it: on success it shows a banner saying this is a demo and no
  exchange takes place; on 404 it shows "open in Telegram".
- **Exposure.** The compose file publishes the app on `127.0.0.1:8000` only, since the demo
  sign-in hands a token to anyone who reaches it. The image is built without `.env`
  (`.dockerignore`).
- **Rates** are live from the ECB and the Bank of Russia: the demo needs internet access and
  shows 503 when a source is unreachable, as a real installation would.

### 1.10 HTTP API

JSON in both directions, property names `snake_case` (`JsonNamingPolicy.SnakeCaseLower`), money
and percentages as strings with their column scale, timestamps ISO 8601 with offset. Routes
marked "auth" need `Authorization: Bearer <token>`.

Request rules, the same for `POST /api/rate` and `POST /api/orders`:

- `give`, `receive`: the codes of section 1.1, upper case.
- `amount_give`: a JSON **string** matching `(0|[1-9][0-9]{0,15})(\.[0-9]{1,2})?` and greater
  than zero (a dot, at most two decimals); anything else, a JSON number included, is
  `invalid_request`.
- `city_id`, `bank_give_id`, `bank_receive_id`, `network_give_id`, `network_receive_id`: JSON
  integers from 1 to 2147483647 (a string or a boolean is not an id). An id that does not
  exist, is inactive, or names a row the side does not allow (section 1.1: channel, currency,
  country) is `invalid_request` with that field.
- Required means the direction's `quote_fields` for `POST /api/rate` and its `order_fields` for
  `POST /api/orders` (as `GET /api/directions` publishes them). A required field the request
  lacks, and any id or detail field the endpoint accepts but the direction does not use (in
  neither its required nor its `optional_fields` - for example `payment_purpose` on an
  `EUR_CASH` order, `wallet_address` on `EUR_WIRE`), are both `invalid_request`, listing every
  such field. JSON properties outside the endpoint's contract (`amount_receive`, `rate`,
  `user_id`, ...) are ignored.

Types: ids are JSON integers (`id`, `order_id`, `telegram_id`; Telegram user ids fit in 52 bits,
so a JavaScript number holds them exactly);
`status` is one of the upper-case statuses of section 1.5; `quote_fields`, `order_fields` and
`optional_fields` are sorted arrays of request property names from the list above plus
`usdt_transfer_time`, `iban`, `recipient_name`, `wallet_address`, `payment_purpose`;
`expires_in` is the token lifetime in seconds (an integer); `provider` is `ecb`, `cbr`, or both
joined by `+` in alphabetical order (`cbr+ecb`) when a direction needs both sources, and
`rate_taken_at` is the older of their snapshot times.

| Method and path | Auth | Request | Success |
|---|---|---|---|
| `GET /healthz` | - | - | 200 `{"status": "ok"}` |
| `GET /api/cities` | - | - | 200 `[{id, name, markup_percent}]`, active only, ordered by id |
| `GET /api/banks` | - | - | 200 `[{id, code, channel, currency, country, markup_percent}]`, active only, ordered by id |
| `GET /api/usdt-networks` | - | - | 200 `[{id, code, fee_percent}]`, active only, ordered by id |
| `GET /api/directions` | - | - | 200 `[{give, receive, quote_fields, order_fields, optional_fields}]`: every valid pair of section 1.1 with the field names a quote needs, an order needs, and an order may carry |
| `POST /api/auth/telegram` | - | `{init_data}` | 200 `{access_token, token_type: "bearer", expires_in}` |
| `POST /api/auth/demo` | - (demo only) | - | 200, same body |
| `GET /api/users/me` | auth | - | 200 `{telegram_id, username, first_name, loyalty_points}` (`username`, `first_name` nullable) |
| `POST /api/rate` | auth | `{give, receive, amount_give, city_id?, bank_give_id?, bank_receive_id?, network_give_id?, network_receive_id?}` | 200 quote: `{give, receive, amount_give, amount_receive, base_rate, effective_rate, markup_percent: {city, bank_give, bank_receive, network_give, network_receive, total, loyalty_discount, applied}, provider, rate_taken_at}` |
| `POST /api/orders` | auth | the quote request plus `usdt_transfer_time?` (`now`, `at_meeting`), `iban?`, `recipient_name?`, `wallet_address?`, `payment_purpose?` | 201 order |
| `GET /api/orders` | auth | - | 200 `[order]`, the caller's own, newest first |
| `GET /api/orders/{order_id}` | auth | - | 200 order |
| `POST /api/orders/{order_id}/cancel` | auth | - | 200 order |
| `POST /api/bot/webhook` | Telegram secret header (not mapped in demo) | a Telegram update | 200; a missing or wrong `X-Telegram-Bot-Api-Secret-Token` is the 401 below (body and header) and changes nothing |

An order is `{id, status, give, receive, amount_give, amount_receive, base_rate, effective_rate,
markup_percent: {city, bank_give, bank_receive, network_give, network_receive,
loyalty_discount}, provider, rate_taken_at, created_at, closed_at, details: {city_id,
bank_give_id, bank_receive_id, network_give_id, network_receive_id, usdt_transfer_time, iban,
recipient_name, wallet_address, payment_purpose}}`, with nulls for fields a direction does not
use and IBAN and wallet masked (section 1.5). An order's `markup_percent` holds the stored
components only; the total and the applied markup are derived from them and not stored.

Error bodies (`INV-21`):

| Status | Body | When |
|---|---|---|
| 401 | `{"error": "unauthorized"}`, header `WWW-Authenticate: Bearer` | no, invalid or expired token; bad `initData`; a missing or wrong webhook secret - the same body and header for every reason |
| 404 | `{"error": "not_found"}` | an order that does not exist **or belongs to someone else** |
| 409 | `{"error": "not_cancellable"}` | cancelling an order that is not `PENDING` |
| 422 | `{"error": "<kind>", "fields": ["body.<field>", ...]}` - every entry `body.`-prefixed | kinds: `invalid_request` (malformed body; a required field missing or an accepted field the direction does not use, per the request rules above; bad field format; a JSON number in a money field), `invalid_direction` (same base; fields `body.give`, `body.receive`), `amount_out_of_range` (outside the limits of section 1.5), `amount_too_small` (the receive amount quantizes to zero) |
| 503 | `{"error": "rates_unavailable", "message": "<localized text>"}` | no fresh rate for a currency the direction needs |
| 500 | `{"error": "internal_error", "request_id": "<id>"}` | any unhandled failure; nothing else is revealed |

### 1.11 Settings

Read from environment variables (and from `.env` in development), once, at startup.

| Variable | Kind | Default |
|---|---|---|
| `BOT_TOKEN` | secret, shape checked (`NR-4`) | none (required outside demo) |
| `BOT_WEBHOOK_SECRET` | secret, 1-256 of `A-Z a-z 0-9 _ -` | none (required outside demo) |
| `JWT_SECRET` | secret, at least 32 characters | none (required outside demo) |
| `PII_ENCRYPTION_KEY` | secret, base64 of exactly 32 bytes | none (required outside demo) |
| `DATABASE_URL` | secret, `postgresql://user:password@host:port/database` (`D-010`) | none (required) |
| `OPERATOR_CHAT_ID` | Telegram chat id for alerts and order messages | none (required outside demo) |
| `OPERATOR_TELEGRAM_IDS` | comma-separated Telegram user ids allowed to press order buttons | none (required outside demo) |
| `APP_ENV` | `development`, `test`, `production`, `demo` | `development` |
| `LOG_FORMAT` / `LOG_LEVEL` | `console` or `json` / `Debug`..`Error` | `console` / `Information` |
| `SERVE_HOST` | listening address; the port is the constant `ServePort` = 8000, not a setting | `127.0.0.1` |
| `ALERT_COOLDOWN_SECONDS`, `RATE_ALERT_COOLDOWN_SECONDS` | alert rate limits | 300, 900 |
| `RATE_FETCH_TIMEOUT_SECONDS` | per rate request | 10 |
| `ECB_*`, `CBR_*` | sections 1.4, 1.4a | as stated there |
| `<CODE>_ORDER_MIN` / `_MAX` | section 1.5 | as stated there |
| `INIT_DATA_MAX_AGE_SECONDS` | `INV-2` | 86400 |
| `JWT_TTL_SECONDS` | token lifetime | 3600 |

`APP_ENV` sets `ASPNETCORE_ENVIRONMENT` (`Development` for `development`, `Production`
otherwise); nothing reads `ASPNETCORE_ENVIRONMENT` directly. Every numeric setting must be
positive, and each `_ORDER_MIN` below its `_ORDER_MAX`; a violation fails startup naming the
variable.

---

## 2. Worst failure mode

**Leaking a secret or a client's personal data** - the bot token, the JWT key, the database
password, or a client's IBAN, name or wallet address - through the repository (including its
history, since the repository becomes public), a log, an error response or the client bundle.
A bot token left as a default value in code is the classic way this happens, and it is the
failure this project is designed against. `RULE-SECRETS` is therefore the top-priority rule, and the
second-worst failure, a silently wrong price, is `RULE-RATE-FRESHNESS`.

---

## 3. Rules

### 3.1 Project rules

| ID | Meaning |
|---|---|
| `RULE-SECRETS` | Every secret comes from the environment, read once into a typed, validated options object at startup, and held in a `Secret` wrapper whose `ToString()` returns `**********`; the app refuses to start if one is missing or a placeholder (demo mode reads none but the password-less `DATABASE_URL`, section 1.9). No secret has a default value in code or in `appsettings*.json`. `.env` is git-ignored; `.env.example` carries placeholders only. Secrets never appear in logs, error responses, the client bundle, test fixtures or commit history. Client PII (IBAN, recipient name, wallet address, payment purpose) is encrypted at rest with AES-256-GCM (`System.Security.Cryptography.AesGcm`) under a key from the environment, and is never logged. A secret that has ever been committed is treated as leaked and rotated |
| `RULE-SERVER-AUTHORITY` | The server trusts nothing the client computes. Identity comes only from a validated `initData` signature or a server-issued JWT (in demo mode only, also from `POST /api/auth/demo`, section 1.9); rate and `amount_to` are recomputed server-side; a user id in a request body is ignored |
| `RULE-RATE-FRESHNESS` | No quote and no order is produced from a rate snapshot older than its source's maximum age (`ECB_RATE_MAX_AGE_SECONDS`, `CBR_RATE_MAX_AGE_SECONDS`) or outside its effective-date window. Unavailability is shown as unavailability, never as a number |
| `RULE-MONEY-DECIMAL` | Money and rates are `decimal` end to end - PostgreSQL `numeric` with explicit precision, JSON as strings in both directions (a JSON number in a money field is refused, `D-007`). Quantization happens in exactly one function |
| `RULE-TEST-FIRST` | The hard classes - the `initData` validator, the rate engine with `Quantize`, the log redaction, and the PII cipher - have their tests written before their implementation, backed by a quoted red run |

### 3.2 Core rules (dev-factory contract)

| ID | Meaning in this project |
|---|---|
| `RULE-INSTALLS` | Project dependencies (NuGet packages, the `dotnet-ef` local tool) are restored into the project and are the agent's. Anything that changes the machine - an SDK, a workload, a global tool, Docker, PostgreSQL - goes to the owner as a checklist (program, exact version, install command), and the agent waits |
| `RULE-GIT` | `main` holds releases and is the owner's: the agent never commits, pushes, merges or opens a pull request into it. `dev` is the integration branch and changes only through a merged pull request. Work happens on short-lived branches from `dev` (`feature/<n>-<slug>`, `fix/`, `chore/`, `docs/`, `refactor/`, `test/`, `ci/`); the agent commits, pushes, opens a pull request into `dev` and merges it once CI is green and the review is `APPROVE`. No force-push, history rewrites, bypassed hooks, or changes to remotes, tags, repository settings, secrets, visibility or releases |
| `RULE-DEPENDENCIES` | NuGet versions live in `Directory.Packages.props` only (Central Package Management), exact versions, no `Version=` in any `.csproj`; the `dotnet-ef` tool version lives in `.config/dotnet-tools.json`; container images are pinned by exact tag in `Dockerfile` and `docker-compose.yml`, and the Testcontainers PostgreSQL image by the same tag as a named constant; GitHub Actions by commit SHA. Restored with `dotnet`, the only package manager. A new or upgraded package lands in the same change, and its pull request lists package + version + why |
| `RULE-GREEN` | No session ends with a failing build or red tests |
| `RULE-PHASE-GATE` | A phase is a set of pull requests merged into `dev`, leaving `dev` buildable and runnable. It ends with a summary, the merged pull requests, `git diff --stat` against where `dev` started, what is demonstrable, the `DECISIONS.md` delta and open questions, then waits for approval. Phases are never chained. A release (`dev` into `main`) is the owner's. A broken environment is reported, never designed around |
| `RULE-REVIEW` | Every commit and gate is preceded by a clean-context review (the `dev-factory:reviewer` agent) of the staged diff against this spec, ending in `APPROVE` or `BLOCK`; a `BLOCK` stops the work until answered |
| `RULE-ASK` | Missing details become one concrete question; undictated choices go to `DECISIONS.md`; unsourced domain facts go to `NEEDS_REVIEW.md` and are marked in the code |
| `RULE-NEGATIVE-TEST` | Every validator has a test proving it rejects bad input |
| `RULE-MUTATION-PROOF` | A guard test is finished only after the line it guards was mutated (`tools/mutate.py` of the plugin) and the test was seen to fail, with the failure quoted |
| `RULE-NO-MAGIC-NUMBERS` | Every constant is named, with its unit in the name (`EcbRateMaxAgeSeconds`, `JwtTtlSeconds`) |
| `RULE-BUILD-HYGIENE` | `TreatWarningsAsErrors`, `EnforceCodeStyleInBuild`, `AnalysisMode=Recommended` and nullable reference types are on for every project; `dotnet format --verify-no-changes` is authoritative and runs in CI. No blanket `#pragma warning disable` or `<NoWarn>`; a targeted suppression carries its reason beside it |
| `RULE-BRIEF` | Reports are changed files plus a short summary |
| `RULE-ENGLISH-ONLY` | All repository artifacts are English - spec, records, README, code comments, XML docs, commit messages, branch names, pull requests, config comments. The Russian texts are data in `wwwroot/locale/ru.json`, the one locale file the front end and the server share (section 1.7) |
| `RULE-LOGGING` | See section 4 |

---

## 4. Logging (`RULE-LOGGING`)

| Aspect | Decision |
|---|---|
| Library | Code logs through `ILogger<T>` with `[LoggerMessage]` source-generated methods (no string-built messages); Serilog is the one provider, configured once in `Logging/LoggingSetup.cs` before the host is built (so startup failures are logged too). `Console` output of any kind (`Console.Write*`, `Console.Out`, `Console.Error`) is banned outside that file (`INV-20`) |
| Destination | stdout in every environment (the container collects it); in `development` also a rolling file `logs/app.log`, 10 MB x 5 |
| Format | JSON lines (`CompactJsonFormatter`) when `LOG_FORMAT=json`; a plain console template when `LOG_FORMAT=console` |
| Levels | `INFO`: startup, each request (method, route template, status, duration_ms - `UseSerilogRequestLogging`), each rate fetch, each order and status change. `WARNING`: rejected input, auth failure, retry. `ERROR`: every caught failure with operation, context and stack |
| Correlation | `request_id` (from `X-Request-ID` when it is a safe token, otherwise generated) pushed to `LogContext` for the whole request and returned in the `X-Request-ID` response header; `order_id` pushed wherever known |
| Top-level handler | An exception-handling middleware logs any unhandled exception at `ERROR` with its stack and returns the 500 body of section 1.10. Process-level: `AppDomain.UnhandledException` and a failed host start are logged before exit |
| Redaction | Done in the pipeline, never by callers, in two layers. (1) A Serilog enricher replaces with `**********` the value of every property whose name matches, case-insensitively and ignoring `_`, `token|secret|password|authorization|initdata|hash|iban|wallet|recipient|purpose|apikey|cookie|connection|databaseurl|url|uri` - recursively through structured values. (2) Every remaining string - each string property value at any depth, the message template's text, and the exception's message and stack - has Telegram bot-token, `/bot<token>/` path, JWT and `postgresql://user:password@` patterns masked before any sink sees the event, so the JSON formatter, which writes properties separately, gets masked values too. Framework categories that log URLs or SQL parameters are raised to `Warning`: `System.Net.Http.HttpClient`, `Microsoft.EntityFrameworkCore`, `Microsoft.AspNetCore.Hosting.Diagnostics`. `EnableSensitiveDataLogging` and Npgsql's `Include Error Detail` are never enabled, so PostgreSQL's `DETAIL` (which holds row values) never reaches an exception message |
| Startup record | version, environment, provider names with their cache and freshness settings, order limits; every secret shown as `**********` |
| Alerting | Every `ERROR` is forwarded to the operator Telegram chat by a dedicated Serilog sink feeding a background queue, rate-limited to one message per fingerprint (source context + message template) per `ALERT_COOLDOWN_SECONDS` (default 300). A failure to alert goes to Serilog's `SelfLog` on stderr only, never back into the pipeline. In demo mode alerts are dropped (section 1.9) |
| Retention | stdout retention is the container platform's; the rolling file is development-only |

---

## 5. Invariants

Each invariant has a test, named in the table; the test is written in the phase that introduces
the behaviour.

| ID | Invariant | Checked by |
|---|---|---|
| `INV-1` | An `initData` whose HMAC does not match, or with no `hash`, is rejected with 401 | `InitDataValidatorTests` |
| `INV-2` | An `initData` with `auth_date` older than `INIT_DATA_MAX_AGE_SECONDS` (86400), or more than 60 seconds in the future (the named constant `InitDataMaxClockSkewSeconds`, not a setting), is rejected | `InitDataValidatorTests` |
| `INV-3` | Every `/api/orders` and `/api/users` route returns 401 without a valid JWT, and a user can never read or change another user's order (404) | `AccessTests` |
| `INV-4` | The order's stored rate equals the server-computed rate for the stored inputs; a client-sent `rate` or `amount_receive` property has no effect | `OrderPricingTests` |
| `INV-5` | With a snapshot older than its source's maximum age, or with an effective date outside the window of section 1.4 or 1.4a, `/api/rate` returns 503 for every direction that needs it and `POST /api/orders` creates nothing | `RateFreshnessTests`, `EcbProviderTests`, `CbrProviderTests`, `RateApiTests` |
| `INV-6` | Every direction row of section 1.1 is accepted with its fields and rejected without each required field; same-base directions are rejected; every detail field rule of section 1.1 rejects a bad value | `DirectionTests`, `DetailFieldTests` (theory data generated from the tables) |
| `INV-7` | No log event emitted by the app contains any value from the redaction list or a bot-token, JWT or credentialed-URL pattern, in properties, message or exception | `LogRedactionTests` |
| `INV-8` | Outside demo mode, the app refuses to start when any required secret is missing, malformed or equals its `.env.example` placeholder, naming the variable and never the value | `SettingsTests` |
| `INV-9` | Only the transitions in section 1.5 succeed; every other pair is rejected and leaves no history row | `StatusMachineTests` |
| `INV-10` | Loyalty points never exceed 10 and are awarded only on `COMPLETED` (`PHASE-6`; until then: points stay 0) | `LoyaltyTests` |
| `INV-11` | Bot webhook calls without the correct `X-Telegram-Bot-Api-Secret-Token`, and callback presses from non-operator ids, change nothing | `BotWebhookTests` |
| `INV-12` | No tracked file contains a Telegram bot-token pattern, a JWT, a PEM private key, or a non-placeholder value for a secret-named key in `.env.example`; the password-less demo `DATABASE_URL` in `docker-compose.yml` is not a secret | `NoSecretsInRepoTests` |
| `INV-13` | PII columns hold ciphertext in the database; decrypting with a wrong key, or a token moved to another column, fails loudly | `PiiCipherTests` |
| `INV-14` | Money values round-trip through the API and database as exact decimals with their column scale; a JSON number in a money field is refused; `Quantize` matches the hand-computed half-down table; no `double`, `float` or `MidpointRounding.ToZero` appears in `Exchange.Domain` or in any file under `src/Exchange.Api` that declares a `decimal` (source scan) | `MoneyTests`, `QuantizeTests` |
| `INV-15` | `POST /api/auth/demo` answers only when `APP_ENV=demo`; in every other environment it returns 404 and creates no user | `DemoModeTests` |
| `INV-16` | With `APP_ENV=demo` the app refuses to start while any production secret of section 1.9 is set or `DATABASE_URL` carries a password; in demo mode nothing is sent to Telegram and no message text is logged | `DemoModeTests` |
| `INV-17` | Every row of the API table of section 1.10 is served with its method, path, auth requirement, success status, and request and response property names and JSON types | `ApiContractTests` |
| `INV-18` | The front end loads nothing from outside `telegram.org` (any scheme, `//host`, WebSockets), keeps the token in no browser or Telegram storage (`localStorage`, `sessionStorage`, `indexedDB`, cookies, `CloudStorage`, `DeviceStorage`, `SecureStorage`), and every locale key it uses exists in `ru.json` - a use is a call of the lookup function `t` with a string literal, and a lookup by a computed key carries a `locale-dynamic: <prefix>` comment whose whole key family must exist; each scan fails if it finds no file, and the key scan fails if it finds no key. `GET /` and every file under `/static` are served | `FrontendScanTests` |
| `INV-19` | No commit in the repository's history adds a bot-token pattern, a JWT, a PEM private key or a file named `.env` (CI checks out the full history) | `NoSecretsInHistoryTests` |
| `INV-20` | Every `PackageReference` has no version of its own and appears in `Directory.Packages.props`; every resolved `Microsoft.AspNetCore.*` and `Microsoft.EntityFrameworkCore*` package in every project's `project.assets.json` is exactly 10.0.11; the forbidden packages of section 6.2 are absent from the restored graph, except `Newtonsoft.Json` reached only through `Microsoft.EntityFrameworkCore.Design` (design-time, `PrivateAssets=all`); no `Console` output outside `LoggingSetup.cs` | `DependencyContractTests` |
| `INV-21` | Every error body of section 1.10 is returned exactly, by every endpoint that can return it | `ErrorContractTests` |

---

## 6. Stack

Fixed; do not substitute. The SDK and runtime are on the owner's machine already (`D-001`).

| Part | Choice | Version |
|---|---|---|
| SDK | .NET SDK, pinned in `global.json` (`rollForward: latestPatch`) | 10.0.400 |
| Runtime | ASP.NET Core / .NET (LTS) | 10.0.11 |
| Web | ASP.NET Core Minimal APIs with route groups; `System.Text.Json` | in-box |
| Data | PostgreSQL; EF Core with Npgsql; EF Core migrations | PostgreSQL 16 (the exact tag `postgres:16.15-alpine`, fixed in `PHASE-1` when the first container arrived (`D-023`) and used by compose and Testcontainers alike); `Microsoft.EntityFrameworkCore` and `Microsoft.EntityFrameworkCore.Relational` 10.0.11, referenced **explicitly** (Npgsql's range `[10.0.4, 11.0.0)` would otherwise resolve 10.0.4 wherever the design package is absent); `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3; `Microsoft.EntityFrameworkCore.Design` 10.0.11 (`PrivateAssets=all`); `dotnet-ef` local tool 10.0.11 |
| Settings | One typed settings object loaded and validated once at startup from the environment variables of section 1.11 by a hand-written loader that reports every problem at once (`D-018`; amended 2026-10-08 from the Options pattern with `ValidateOnStart`, approved by the owner) | in-box |
| HTTP out | `IHttpClientFactory` typed clients; retries hand-written | in-box |
| Auth | `Microsoft.AspNetCore.Authentication.JwtBearer` - HS256, `JWT_TTL_SECONDS` tokens, re-issued silently from `initData`; `initData` HMAC with `HMACSHA256` and `CryptographicOperations.FixedTimeEquals` | 10.0.11 |
| Crypto | `System.Security.Cryptography.AesGcm` (PII), `RandomNumberGenerator` (demo keys) | in-box |
| XML | `System.Xml` with DTD processing prohibited | in-box |
| Logging | `Serilog.AspNetCore` (brings Serilog 4.3.0, Console, File, Compact formatter) | 10.0.0 |
| Clock | `TimeProvider` everywhere time matters; `Microsoft.Extensions.TimeProvider.Testing` in tests | in-box; 10.10.0 |
| Frontend | Plain HTML/CSS/JS from `wwwroot`; Telegram WebApp JS from `telegram.org` only | - |
| Tests | xUnit v3 on Microsoft.Testing.Platform (`global.json` `test.runner`), Shouldly, `Microsoft.AspNetCore.Mvc.Testing` (`WebApplicationFactory`), Testcontainers for a real PostgreSQL | `xunit.v3` 4.0.1, `Shouldly` 4.3.0, `Microsoft.AspNetCore.Mvc.Testing` 10.0.11, `Testcontainers.PostgreSql` 4.15.0 |
| Lint/format | SDK analyzers (`AnalysisMode=Recommended`), `.editorconfig`, `dotnet format` | in-box |
| Packaging | Multi-stage `Dockerfile` (`mcr.microsoft.com/dotnet/sdk:10.0.400` build, `mcr.microsoft.com/dotnet/aspnet:10.0.11` runtime, non-root `$APP_UID`) and `docker-compose.yml` for the demo; no deployment | - |
| CI | GitHub Actions on `ubuntu-latest`: restore, `dotnet format --verify-no-changes`, build, test; actions pinned by SHA; `setup-dotnet` pinned to 10.0.400; full-history checkout for `INV-19` | - |

### 6.1 Solution layout

```
ExchangeMiniApp.slnx
global.json  Directory.Build.props  Directory.Packages.props  .editorconfig  .gitattributes
.config/dotnet-tools.json
src/Exchange.Domain/         pure C#: directions, detail field rules, rate engine, Quantize,
                             status machine, limits; no I/O, no EF, no ASP.NET (fast tests; most
                             hard classes live here, the redaction and the PII cipher live in
                             the host)
src/Exchange.Api/            the host: settings, logging, endpoints, auth, EF Core (Data/,
                             Data/Migrations/), rate providers, bot, wwwroot/
tests/Exchange.Domain.Tests/ unit tests, no Docker
tests/Exchange.Api.Tests/    WebApplicationFactory + Testcontainers + repository scans
Dockerfile  docker-compose.yml  .dockerignore  .env.example
```

### 6.2 Known conflicts and prohibitions

- **VSTest packages are forbidden.** `xunit.v3` runs on Microsoft.Testing.Platform, which the
  .NET 10 SDK selects through `global.json`; `Microsoft.NET.Test.Sdk` and
  `xunit.runner.visualstudio` belong to the VSTest path and must not be added. Do not add
  `--nologo` to `dotnet test`: under the platform it reports zero tests and exits 5. A test
  project that discovers no test fails the run, so each test project has real tests from the
  phase that creates it.
- **Microsoft packages track the installed runtime.** ASP.NET Core and EF Core packages are
  pinned to 10.0.11, the runtime on the owner's machine and in the runtime image, not to the
  newest patch on NuGet. They move together, in one pull request, when the SDK pin moves.
- **SDK roll-forward.** `global.json` uses `rollForward: latestPatch`, so a machine with a newer
  10.0.4xx SDK may build with it; CI pins 10.0.400 so that CI and the runtime image match the
  owner's machine exactly.
- **Forbidden, with substitutes:** `FluentAssertions` (paid licence) -> Shouldly; `MediatR`,
  `AutoMapper` (commercial licences) -> plain handlers and hand-written mapping; `Moq` (the
  2023 telemetry incident) -> hand-written fakes; `Newtonsoft.Json` -> `System.Text.Json` (it
  arrives transitively with the EF design package, design-time only; no project may reference or
  use it); `Polly` / `Microsoft.Extensions.Http.Resilience` -> the hand-written retry of section
  1.5; `Telegram.Bot` -> a small typed client over `HttpClient` for the Bot API methods used;
  `Swashbuckle` and any OpenAPI UI -> none (the contract is section 1.10); any CDN except
  `telegram.org`; `MidpointRounding.ToZero` in money code (section 1.3).
  `DependencyContractTests` enforces the package list (`INV-20`).
- Storing the JWT in any browser storage is forbidden (memory only, `D-003`); `BinaryFormatter`
  and any deserializer of untrusted types are forbidden.
- The root `.gitattributes` normalizes text to LF (`* text=auto eol=lf`), so files hash and diff
  the same on Windows and in CI.

---

## 7. Phases

### PHASE-0 - Skeleton, settings, logging, CI

- What: the solution layout of section 6.1 with build hygiene on; typed settings with fail-fast
  secret validation (`Secret` wrapper); Serilog with redaction, request logging, correlation,
  the top-level handler and the alert sink (delivery stubbed to a log line until `PHASE-3`);
  `GET /healthz`; the repository scans; CI. The money `Quantize` function lands here too: it is
  pure, every later phase depends on it, and it gives `Exchange.Domain.Tests` real tests from
  the start.
- Deliverables: `ExchangeMiniApp.slnx`, `global.json`, `Directory.Build.props`,
  `Directory.Packages.props`, `.editorconfig`, `src/Exchange.Api/Program.cs`, `Settings/`,
  `Logging/`, `src/Exchange.Domain/Money/Quantize.cs`; `.env.example`;
  `.github/workflows/ci.yml`; `.gitignore` and `.dev-factory.json` updated for the solution
  (both exist from the bootstrap); tests for
  `INV-7`, `INV-8`, `INV-12`, `INV-19`, `INV-20`, `QuantizeTests` of `INV-14`, and `INV-21` for
  the 500 body.
- Gate: `dotnet build --nologo` and `dotnet test --no-build` are green locally and in CI;
  starting with a missing secret exits non-zero with an error naming the variable; `GET /healthz`
  returns 200 and the startup log line shows the version and masked secrets.
- **Closed 2026-10-08**, approved by the owner. Pull requests #4 (skeleton, `Quantize`, `INV-20`,
  CI), #5 (settings, `INV-8`, `INV-12`, `INV-19`) and #6 (logging, `INV-7`, the 500 body of
  `INV-21`), each with a clean-context review and green CI. Gate run on `dev` at `92ff471`: build
  0 warnings, 251/251 tests; started with no secrets, the app exited 1 with one `FATAL` line
  naming all seven missing variables and no value; started with every setting, `GET /healthz`
  answered `200 {"status":"ok"}` and the startup record showed version
  `0.1.0+92ff471d61276de93799cd43f4f427840a01fdf5` with every credential as `**********`. The
  owner also approved the amendment of the section 6 Settings row (`D-018`).

### PHASE-1 - Database and reference data

- What: EF Core model of the whole schema (enumerations as text with `CHECK` constraints),
  numeric precisions, the PII value converter (AES-GCM, version byte, column-bound associated
  data), the initial migration with seed data, the `migrate` command, public reference endpoints.
- Deliverables: `src/Exchange.Api/Data/`, `Data/Migrations/`, `GET /api/cities`,
  `GET /api/banks`, `GET /api/usdt-networks`, `GET /api/directions`; tests for `INV-13`,
  `INV-14`, and `INV-17` for these rows.
- Gate: `dotnet run -- migrate`, run in `src/Exchange.Api`, on an empty PostgreSQL creates and
  seeds every table; the three reference-data endpoints return only active rows and
  `/api/directions` returns the direction table, each in the shape of section 1.10.
- Generated migrations are reviewed by hand before they are committed: the pull request quotes
  the SQL of `dotnet ef migrations script` and the reviewer reads it.
- **Closed 2026-10-09**, approved by the owner. Pull requests #8 (directions in the domain), #9
  (PII cipher, the money source scan), #10 (schema, migration with the seed, `migrate`; its SQL
  quoted and reviewed), #11 (reference endpoints, the `INV-17` contract harness), each with a
  clean-context review and green CI. Gate run on `dev` at `b5fe3cd`: build 0 warnings, 502/502
  tests; on an empty `postgres:16.15-alpine`, `dotnet run -- migrate` in `src/Exchange.Api` exited
  0 with one log line, `Database migrated: 1 migration(s) applied 20261008212257_InitialSchema`,
  and left 6 cities, 5 banks and 3 networks; with one city made inactive, the running app's
  `GET /api/cities` listed the other five, money as strings at scale 4, and `/api/banks`,
  `/api/usdt-networks` and `/api/directions` answered 5, 3 and 14 rows in the shape of section
  1.10.

### PHASE-2 - Telegram authentication

- What: `initData` validator, user upsert, JWT issue and verification.
- Deliverables: `POST /api/auth/telegram`, `GET /api/users/me`; tests for `INV-1`, `INV-2`,
  `INV-3`, and `INV-21` for 401.
- Gate: a real `initData` from the project's own test bot authenticates; a byte-altered copy
  gets 401.
- **Closed 2026-10-09**, approved by the owner. Pull requests #13 (the `initData` validator,
  `D-028`), #14 (JWT issue and verification, `POST /api/auth/telegram`, `GET /api/users/me`, the
  401 of `INV-21`, `INV-3` for `/api/users`, the auth column of `INV-17`, `D-029`), each with a
  clean-context review and green CI. Gate run on `dev` at `62551c5`: build 0 warnings, 587/587
  tests; the app in `production` mode with the test bot's token, on an empty
  `postgres:16.15-alpine`, answered a real `initData` - which carried a `signature` field, signed
  into its data-check-string as `D-028` assumed - with 200 and a token that `GET /api/users/me`
  accepted with 200; copies with one byte changed in `auth_date` and in `hash` got 401,
  `{"error":"unauthorized"}` and `WWW-Authenticate: Bearer`. The app log held no bot token and no
  JWT, and two `WARNING` records with the reason `bad_signature`.

### PHASE-3 - Rate engine, ECB and Bank of Russia providers

- What: the pure rate engine in `Exchange.Domain`; `IRateProvider` with the ECB and Bank of
  Russia providers; cache and freshness guard per provider; `POST /api/rate` with breakdown;
  Telegram delivery of alerts.
- Deliverables: `src/Exchange.Domain/Rates/`, `src/Exchange.Api/Rates/`, `Alerts/`; tests for
  `INV-5`, `INV-6`, both effective-date windows, the rate-engine matrix, and `INV-21` for the
  422 and 503 bodies of `/api/rate`.
- Gate: a live quote for each direction, `USD`/`EUR` priced from the ECB and `RUB` from the Bank
  of Russia; with each provider in turn pointed at an unreachable URL, its last snapshot is
  served until its max age, then 503 for exactly the directions that need it, and an `ERROR`
  alert reaches the operator chat.

### PHASE-4 - Orders and operator bot (key milestone)

- What: order creation with server-side pricing, the status machine with history under a row
  lock, operator notification with inline buttons, bot webhook, client status messages,
  notification queue with retry, a development poller (`dotnet run -- poll`, run in
  `src/Exchange.Api`) for running without a public address.
- Deliverables: `src/Exchange.Domain/Orders/`, `src/Exchange.Api/Orders/`, `Bot/`; tests for
  `INV-4`, `INV-9`, `INV-11`, and `INV-21` for the order endpoints' 404, 409 and 422 bodies.
- Gate: an order placed via the API appears in the operator chat; pressing Confirm, then
  Processing, then Complete (the buttons offer only the transitions of section 1.5) updates the
  order and messages the client each time; a press from a non-operator account changes nothing.

### PHASE-5 - Mini App front end and demo mode

- What: the front end of section 1.7, served from `wwwroot`; demo mode of section 1.9.
- Deliverables: `src/Exchange.Api/wwwroot/`, `Demo/`; tests for `INV-15`, `INV-16`, `INV-18`;
  `INV-17` covers every row of section 1.10.
- Gate: on a real phone inside Telegram (the app behind a `cloudflared` quick tunnel, the bot's
  menu button pointed at it), a full order is placed for `RUB_WIRE -> EUR_CASH` and
  `USDT -> EUR_ATM`, and the history shows both; with `APP_ENV=demo` the same two orders are
  placed in a desktop browser.

### PHASE-6 - Loyalty and admin panel (deferred)

Deferred; not part of v1 (`L-3`). Kept so that the scope stays visible.

- What: points on completion, discount in the quote (section 1.6), Razor Pages admin under
  `/admin` with one account (section 1.8).
- Deliverables: `src/Exchange.Api/Admin/`; tests for `INV-10`.
- Gate: completing orders moves points and the quoted rate as section 1.6 specifies; an admin
  edit to a markup changes the next quote.

### PHASE-7 - Demo packaging, portfolio README, publication readiness

- What: non-root multi-stage `Dockerfile`, `.dockerignore`, `docker-compose.yml` (database on
  `tmpfs` with trust authentication, no published database port, app on `127.0.0.1:8000`); the
  portfolio README in English with screenshots, the rate sources named and the limitations
  table; the publication checklist for the owner.
- Deliverables: `Dockerfile`, `.dockerignore`, `docker-compose.yml`, `README.md`,
  `docs/screenshots/`; `INV-19` green on the full history.
- Gate: on a clean clone, `docker compose up` alone starts the demo; in a desktop browser at
  `http://localhost:8000` an order is placed for `RUB_WIRE -> EUR_CASH` and `USDT -> EUR_ATM` at
  live ECB and Bank of Russia rates and the history shows both; a second `docker compose up`
  starts with an empty history; the owner has read the README. Then the owner releases `dev`
  into `main` and makes the repository public - both are hers (`RULE-GIT`).

---

## 8. Limitations

| ID | Limitation | Severity | Workaround |
|---|---|---|---|
| `L-1` | `USD` and `EUR` are priced from the ECB's daily reference rates: published for information once a TARGET business day, not market rates, and they do not move between publications | medium | The office's markups absorb the gap; freshness guard and operator alerts; a paid market-data provider replaces it behind `IRateProvider` when there is a budget |
| `L-2` | No payments are processed; deals are completed by the operator outside the app | by design | - |
| `L-3` | Loyalty and the admin panel are specified but not built (`PHASE-6` deferred): no discount is applied, reference data changes by migration only | medium | The Profile tab shows points as stored (0) |
| `L-4` | PII purge after 90 days and PII key rotation are not automated | medium | Both need a scheduled job and a re-encryption tool, written when the app is deployed; nothing is deployed |
| `L-5` | `RUB` is priced from the Bank of Russia's official daily rate, not a market mid; it changes once a business day, may differ from the market, and `cbr.ru` may refuse some foreign data-centre networks | high | The office's `RUB` bank markups absorb the gap (`NR-8`); freshness guard and alerts as for the ECB |
| `L-6` | The front end is not driven by browser automation in the test suite; it is held by static scans (`INV-18`) and checked by hand at the `PHASE-5` and `PHASE-7` gates | low | The API carries the rules, and `INV-17` and `INV-21` hold the contract the page relies on |
| `L-7` | Nothing is deployed; the demo runs locally. GitHub branch protection for a private repository needs a paid plan, so until the repository is public `main` and `dev` are protected by the dev-factory guard and review only | low | The owner enables branch protection when the repository becomes public |

---

## 9. Open questions

| ID | Question | Answer |
|---|---|---|
| `Q-1` | Which licence does the public repository carry? | **Closed 2026-10-08:** MIT (in the repository's first commit) |
| `Q-2` | Which Telegram bot do the `PHASE-2`..`PHASE-5` gates use? | **Closed 2026-10-08:** a new test bot of this project's own, created by the owner in BotFather |
| `Q-3` | Does the README link to other repositories? | **Closed 2026-10-08:** no; the repository stands on its own |

---

## 10. Working protocol

- Read this document fully before writing any code; it is the source of truth. `DECISIONS.md`
  records choices the spec did not make; `NEEDS_REVIEW.md` holds unverified domain claims.
- Before each phase: post a short plan (pull requests, files per pull request) and wait for the
  go-ahead.
- One task per work branch, one logical change per commit, Conventional Commits in English.
- Commit protocol: stage -> `dev-factory:reviewer` on the staged diff -> stamp the verdict with
  the plugin's `review_gate.py` -> commit without `-a`. Pull requests and merges go through the
  plugin's `tools/ship.py` (push, pull request into `dev`, wait for CI on the pushed commit,
  merge).
- Dependencies: the pull request that adds or upgrades a package names it, its version and why.
- On an environment blocker (Docker not running, a gate needing the owner's bot): stop and
  report. Never change the architecture to route around a broken environment.
- Gates that need the owner (a bot token in `.env`, button presses, a phone) are scripted in the
  session scratchpad so that the owner only performs the human step; scripts print statuses,
  never secrets.
- The dev-factory guard refuses `dotnet ... --project <path>` (it reads it as aiming a package
  manager at another project), so per-project commands are run from the project's directory
  (`cd src/Exchange.Api && dotnet run -- migrate`).

### 10.1 Effort

Estimates in working sessions of the agent. Integration with external services (Telegram, the
two banks' feeds, Testcontainers in CI) is the systematic underestimate: multiply those parts
by 2.

| Sessions | Phase | Underestimated part |
|---|---|---|
| 1 | `PHASE-0` | CI with Testcontainers, analyzer noise on first build |
| 1 | `PHASE-1` | the PII converter and migration review |
| 0.5 | `PHASE-2` | the owner's bot setup |
| 1.5 | `PHASE-3` | the two feeds' formats and freshness windows |
| 1.5 | `PHASE-4` | the bot's callback flow and the interactive gate |
| 1 | `PHASE-5` | the phone gate (tunnel, BotFather menu button) |
| 1 | `PHASE-7` | screenshots and the README |

### 10.2 Bootstrap

`RULE-GIT` keeps `main` and `dev` out of the agent's reach, so the repository starts in four
steps, two of them the owner's:

1. Agent: `gh repo create OlgaAntonikova/exchange-miniapp-dotnet --private --add-readme
   --license mit` (GitHub makes the first commit on `main`; the agent commits nothing there).
   Done 2026-10-08.
2. Owner: creates `dev` from `main` on GitHub.
3. Agent: clones the repository, branches `chore/1-spec` from `dev`, adds the spec set
   (`docs/SPEC.md`, `DECISIONS.md`, `NEEDS_REVIEW.md`, `README.md`, `.dev-factory.json`),
   `.gitignore` and `.gitattributes`, and opens the first pull request into `dev`. CI arrives
   with `PHASE-0`; until it exists, the merge is the owner's.
4. `PHASE-0` starts after the owner's go-ahead on its plan.

Nothing needs installing: .NET SDK 10.0.400, Docker Desktop, `gh` and `cloudflared` are on the
owner's machine already; the `dotnet-ef` tool is restored locally from the tool manifest.

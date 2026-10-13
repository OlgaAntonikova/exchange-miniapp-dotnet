# Needs review

Domain claims that nobody qualified has verified yet. Each is marked in code where it is used.

| ID | Claim | Used in | Who can verify |
|---|---|---|---|
| `NR-1` | USDT is priced 1:1 against USD for the purpose of the base rate | rate engine normalisation | the office owner |
| `NR-2` | The seeded markups (cities, banks, USDT networks) are the office's current prices | seed migration | the office owner |
| `NR-3` | Receive amounts are rounded to 2 decimals, to nearest with ties toward zero | `Quantize` | the office owner |
| `NR-4` | A Telegram bot token is `<numeric bot id>:<at least 30 characters of A-Z, a-z, 0-9, _ and ->`. Telegram documents it only by example; the check is kept loose so a valid token is never refused | settings bot-token shape check | Telegram Bot API documentation / a BotFather-issued token |
| `NR-5` | The Bank of Russia sets official rates for a calendar date (Moscow), usually publishing them the business day before; a rate stays in force until the next one, the New Year holidays (about 10 days) being the longest run without a new rate; `XML_daily.asp` answers `ValCurs Date="dd.mm.yyyy"` with `Valute`/`Nominal`/`Value` (decimal comma) in windows-1251; Moscow is UTC+3 all year | Bank of Russia provider, its effective-date window | the Bank of Russia's rate-setting regulation and the live endpoint |
| `NR-6` | A wallet address for the supported USDT networks is 20-128 characters of letters, digits and `_ + / = : -`; the check is deliberately loose and does not tell networks apart | detail field rules | the networks' address specifications / the office |
| `NR-7` | The ECB publishes its reference rates at about 16:00 Frankfurt time on TARGET business days, and the longest closing is Good Friday to Easter Monday, so a rate is never more than 5 days old when the source works; `eurofxref-daily.xml` is a gesmes envelope (`http://www.gesmes.org/xml/2002-08-01`) holding one `Cube time="yyyy-mm-dd"` of `Cube currency rate` rows in `http://www.ecb.int/vocabulary/2002-08-01/eurofxref`, each in units per one EUR | ECB provider freshness window | the ECB's published TARGET calendar and the live endpoint |
| `NR-8` | The seeded `RUB` sender-bank markups (SBER 0.0, TINKOFF 0.5) are right for the Bank of Russia's official rate; their values have not been checked against an official rate. To be adjusted in the admin panel (`PHASE-6`) | seed migration, `RUB_WIRE` quotes | the office owner |
| `NR-9` | The Bank of Russia's official rates may be shown in a public project with the source named, as the README does; its reuse terms were not checked | `README.md` rate sources | the Bank of Russia's published terms of use |

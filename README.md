# Exchange Mini App

A Telegram Mini App for a currency-exchange office in Montenegro, built with ASP.NET Core 10,
EF Core and PostgreSQL. A client picks what they give and what they receive, sees a live quote
with every markup itemised, and places an exchange request; the office's operator handles it
from a Telegram chat. The app never moves money.

**Status:** specification written ([docs/SPEC.md](docs/SPEC.md)); implementation in progress.
The full README, with how to run the demo, arrives in `PHASE-7`.

## Build and test

Requires the .NET SDK pinned in `global.json` and Docker (the integration tests start a real
PostgreSQL).

```bash
dotnet build --nologo
dotnet test --no-build
```

## Limitations

| ID | Limitation |
|---|---|
| `L-1` | `USD` and `EUR` are priced from the ECB's daily reference rates, not market rates |
| `L-2` | No payments: deals are completed by the operator outside the app |
| `L-3` | Loyalty discount and admin panel are specified but not built |
| `L-4` | PII purge and PII key rotation are not automated |
| `L-5` | `RUB` is priced from the Bank of Russia's official daily rate, not a market mid |
| `L-6` | The front end is checked by static scans and by hand, not by browser automation |
| `L-7` | Nothing is deployed; the demo runs locally. Until GitHub branch protection is enabled, `main` and `dev` are protected only by the dev-factory guard and review |

Severities and workarounds: section 8 of [the specification](docs/SPEC.md).

## License

MIT - see [LICENSE](LICENSE).

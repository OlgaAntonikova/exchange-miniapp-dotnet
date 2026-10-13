# Publication checklist

The steps that take this repository from a private project to a public portfolio piece
(`PHASE-7`). Every step is the owner's: `main`, the repository's visibility and its settings are
outside the agent's reach (`RULE-GIT`).

## Before release

1. **The demo works on a clean clone.** In an empty directory:
   `git clone`, then `docker compose up`, then <http://localhost:8000> in a desktop browser:
   orders for `RUB_WIRE -> EUR_CASH` and `USDT -> EUR_ATM` at live rates, both in the history;
   after `Ctrl+C` and another `docker compose up`, the history is empty (the `PHASE-7` gate).
2. **The README reads well** to someone who has never seen the project, including the
   screenshots, the rate sources and the limitations.
3. **CI is green on `dev`**, including `NoSecretsInHistoryTests` (`INV-19`), which scans every
   commit for a bot token, a JWT, a private key or a `.env` file.
4. **Nothing private is in the history.** Beyond the automated scan, skim
   `git log --stat` for files that should not be public. The bot token was never committed; the
   local `src/Exchange.Api/.env` is ignored by git and by Docker.

## Release

5. **Release `dev` into `main`:** a pull request from `dev` into `main`, merged once CI is green.
6. **Make the repository public:** Settings, General, Danger Zone, Change visibility.

## Right after

7. **Branch protection** for `main` and `dev` (`L-7`): Settings, Branches. Require a pull
   request and the `build-and-test` check; disallow force pushes and deletion.
8. **Security features:** Settings, Code security. Turn on secret scanning with push protection,
   and Dependabot alerts.
9. **About:** a one-line description, the topics (for example `aspnetcore`, `telegram-mini-app`,
   `efcore`, `postgresql`, `docker`), and no website link, since nothing is deployed.
10. **The test bot:** its menu button points at a placeholder address; nothing else needs
    changing. Revoking its token in BotFather is optional, since the token never left the
    owner's machine.

## Open items that stay open

- The domain assumptions in [NEEDS_REVIEW.md](../NEEDS_REVIEW.md) await the office's
  confirmation; the README links to them.
- `PHASE-6` (loyalty and the admin panel) stays deferred (`L-3`).

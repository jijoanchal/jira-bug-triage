# Failure Triage

Upload a Playwright JSON report → AI turns each failure into a Jira-ready bug → review, edit, merge → create in Jira with artifacts attached.

- `api/` – .NET 9 Web API (EF Core, SQLite by default, JWT auth)
- `web/` – Angular 22 SPA (standalone, signals, zoneless)
- `samples/` – a sample report plus one artifact to try the flow

## Environment variables (read by the API)

| Variable | Purpose |
|---|---|
| `ANTHROPIC_API_KEY` | AI triage and merge. `ANTHROPIC_MODEL` optionally overrides the model (default `claude-sonnet-5-5`). |
| `CONFLUENCE_API_URL`, `CONFLUENCE_API_USER`, `CONFLUENCE_API_TOKEN` | Used as the Jira credentials (Atlassian Cloud basic auth). A trailing `/wiki` is stripped to get the Jira site URL. |
| `JIRA_BASE_URL`, `JIRA_API_USER`, `JIRA_API_TOKEN` | Optional; take priority over the `CONFLUENCE_*` values. |
| `JIRA_PROJECT_KEY` | Optional default project key. It can also be typed in the UI when creating bugs. |
| `JIRA_ISSUE_TYPE` | Optional, default `Bug`. |
| `JIRA_AFFECTS_VERSION` | Optional default for projects that require *Affects Version/s* (comma-separated names that exist in the project). It can also be typed in the Create-in-Jira dialog. |
| `JIRA_DUPLICATE_CHECK`, `JIRA_DUPLICATE_STATUSES`, `JIRA_DUPLICATE_THRESHOLD` | Duplicate detection before creating an issue (on by default; `false` disables). Statuses that count as "being handled" default to `Open, To Do, Planned for Sprint, Selected for Development, In Progress, Dev In Progress` (matched by name). The summary-similarity threshold defaults to `0.6`. |
| `JIRA_DOSSIER_MINUTES` | How long the saved dossier is trusted before the list of open tickets is checked in Jira again (default `10`; `0` = every time). |
| `JIRA_STORY_LINK_TYPE` | Issue link type used to tie a new bug to its story (default `Relates`). |
| `JIRA_EXTRA_FIELDS` | Optional JSON object merged into every issue's fields, for other required fields, e.g. `{"components":[{"name":"Web"}]}`. |
| `ARTIFACTS_ROOT` | Optional folder the server may read artifacts from when they are not uploaded with the report. |
| `ADMIN_EMAIL`, `ADMIN_PASSWORD` | Seed admin on first start (default `admin@triage.local` / `Admin@12345`, must be changed at first sign-in). |
| `JWT_SECRET` | Optional. If unset, a key is generated once and stored in `api/App_Data/jwt.key`. |

Secrets are only ever read on the server and are never returned to the browser.

## Run

Build and start the single-host app:

```powershell
.\build-and-start.ps1
```

The script builds the Angular app, copies it into `api/wwwroot/`, builds the API, and starts it at
http://localhost:5145. It runs `npm ci` if frontend dependencies are missing. Use `-BuildOnly` to build without
starting the API, `-SkipWeb` to reuse the existing `api/wwwroot/`, or `-Url` to choose another listening URL.

For frontend development with hot reload, use two terminals instead:

```powershell
cd api; dotnet run --urls http://localhost:5145
cd web; npm start          # http://localhost:4200, proxies /api to :5145
```

## Package and deploy to IIS

```powershell
.\publish.ps1                       # builds web + API -> publish\FailureTriage-deploy-<timestamp>.zip (app + deploy-iis.ps1)
.\deploy-iis.ps1 -CopyEnvFromSession    # elevated PowerShell on the IIS server
.\deploy-iis.ps1 -SiteName Triage -HostName triage.company.com -Port 80 -EnvVars @{ ANTHROPIC_API_KEY = 'sk-ant-...' }
```

`install-prereqs.ps1` installs what the server needs (IIS role + management scripting tools, ASP.NET Core 9 Hosting Bundle;
`-OpenFirewallPort 5050` also opens the firewall). The deploy script creates a No Managed Code app pool and the
site, copies the package, and runs a smoke test; re-running it updates the site in place. The database, JWT key and
artifacts live in `-DataPath` (default `C:\inetpub\FailureTriageData`, set through `DataDir`), outside the site folder, so
redeploys never overwrite them. Settings and secrets are stored as app pool environment variables and are kept across
redeploys. Back up the data folder.

## Using it

1. **Runs → New run**: choose the Playwright JSON. Add artifacts as files, a `.zip`, or the `test-results` folder.
   Artifact paths in the report come from the machine that ran the tests, so they are matched by trailing path
   (e.g. `test-results/login-chromium/test-failed-1.png`). Inline base64 attachments are used as-is.
2. AI writes a summary, description, labels and severity per failed test. If AI fails for a test, a draft is built
   from the raw error and flagged, with a **Re-triage** button.
3. Open a bug to edit summary, description, severity and **labels** (tag input; Jira-safe lowercase, no spaces).
4. Tick two or more bugs → **Merge selected**: AI writes one combined bug; the originals stay in history as *Merged*.
5. **Preview / PDF** shows the cleansed report; **Export PDF** opens the browser print dialog – choose *Save as PDF*.
6. Tick bugs → **Create in Jira**. Each bug ends as *Created*, *Created, attachments failed* (issue exists, only the
   missing attachments are retried) or *Jira failed*, with the error text and a full attempt log. **Retry** is per bug,
   or **Retry all failed** for the run.
   The Create/Retry dialog takes the project key, **Affects version** and **Fix version**; once the project key is
   valid the version boxes offer the project's versions from Jira (editable, you can also type one). **Reopen** on a
   submitted bug makes it editable and "Not sent" again so it can be resubmitted with different details (an issue that
   was already created stays in Jira and is detached from the bug).
   **Duplicates:** the project's open bugs (issue type `JIRA_ISSUE_TYPE`, in Open / Planned for Sprint / In Progress etc.) are kept in a
   per-project *dossier* saved as `{DataDir}/jira-dossier/{KEY}.json`, so it survives restarts and redeploys. It always mirrors Jira's
   open tickets: every `JIRA_DOSSIER_MINUTES` (default 10; 0 = every check) a sync asks Jira only which tickets are open
   (key, status, updated stamp), downloads summary and description just for new or edited tickets, reuses the saved copy for the rest,
   and drops tickets that were closed, so nothing needs purging by hand. Each bug is then compared with the dossier in memory, and issues the
   app creates are added immediately. If Jira can't be reached during a sync the saved copy is left untouched. `GET /api/jira/dossier?projectKey=KEY[&refresh=true]`
   shows or refreshes it (deleting the file just forces a full rebuild). The match is first by a `tf-…` label that this app puts on every issue it creates
   (one per failing test), then by how alike the summaries are. If one is found the bug becomes **Already in Jira**, points at
   that ticket, and no new issue is created (the existing ticket is not modified). Tick *Create new issues even if an open one
   already exists* in the dialog to skip the check; if the search itself fails, the issue is created and the attempt log says so.
   **Stories:** story keys (e.g. `GCR-123`) are read from the CSV/JSON (fields named story, requirement, epic, issue, linked,
   coverage, ticket or jira, and Playwright `story`/`issue` annotations). Each new Jira bug is linked to its story with
   `JIRA_STORY_LINK_TYPE`. A bug with no story ID is flagged in a highlighted box on the bug and in Preview / PDF that asks for
   it; the Create dialog warns too. A story entered after the bug is in Jira is linked with **Link to story in Jira**.
7. **Dashboard** charts every iteration (test results and bug/Jira status) with a history table.

## AI usage, cost and governance

Every Anthropic call (triage, re-triage, merge) is logged in the `AiUsage` table with the user, run/bug, model, status,
retries, input / cache-write / cache-read / output tokens, estimated cost and duration. Failed calls are logged too
(the tokens of a reply that could not be parsed are still counted). **AI usage** in the menu shows it per month (UTC):
totals, by user / model / operation / day, the call-by-call log and a CSV export. Non-admins see only their own usage.

Configured under `Ai` in `appsettings.json`:

| Setting | Purpose |
|---|---|
| `Models` | Price list in USD per million tokens (input, output, cache read; cache writes are 1.25x input). **Check these against current Anthropic pricing.** A model with no entry is logged with cost $0 and flagged *unpriced*. |
| `MonthlyBudgetUsd` | Org-wide monthly limit (null = none). An admin can override it on the AI usage page. |
| `EnforceBudget` | `false` (default): limits only warn (header chip at `WarningPercent`, default 80%). `true`: AI calls are refused once a limit is reached and logged as *blocked*. |

Admins set the global limit and per-user limits on the AI usage page (Governance). The tables are created automatically,
including on databases created before this feature existed.

## Notes

- Database provider is configuration: `Database:Provider` = `Sqlite` | `SqlServer` | `Postgres`, plus
  `ConnectionStrings:Default`. The schema is created with `EnsureCreated`; switch to EF migrations before running
  against a shared database that must evolve.
- Jira Cloud REST v3 is used (ADF description, `/attachments` upload). Jira Server/Data Center would need small changes.
- Attachment upload limits are Jira's (commonly 10 MB per file); oversized files show as upload failures and can be retried.
- Roles: `Admin` manages users; `User` triages. Deactivating a user blocks their existing sessions immediately.

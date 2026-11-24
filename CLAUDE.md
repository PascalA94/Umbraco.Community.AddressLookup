# CLAUDE.md

`Umbraco.Community.AddressLookup`: An Umbraco Forms address lookup field with Loqate and Ideal Postcodes behind a server-side proxy.
Razor class library in `src/Umbraco.Community.AddressLookup/` (`Core/` is line-agnostic, `Web/` is per Umbraco line).
Design `docs/design.md`, plan `docs/plan.md`, spikes `docs/spikes.md`.

**Never commit, push or publish.** Pascal commits, pushes, tags and releases.

Token rules and the 14 CI rules live in the Umbraco Packages workspace README, sections "Token rules" and "Build approach > CI and GitHub rules".

## Lines and versions

- **One branch, two lines.** `UmbracoMajor` (`17` or `18`, default `18`, set as a property or an environment variable) picks the CMS range (`[17.8.0, 18.0.0)` or `[18.3.0, 19.0.0)`), the package version (`17.x.y` or `18.x.y`), the `UMBRACO_17` / `UMBRACO_18` constants and its own `packages.<line>.lock.json`, `obj/<line>` and `bin/<line>`. `eng/lines.txt` lists the lines; `bash build/test.sh` runs the suites once per line.
- Both lines ship from `main` and one tag; there is no `v17` branch. Floors live in `Directory.Build.props` (CMS 17.8.0 / 18.3.0, Forms 17.6.2 / 18.2.2).
- A version bump touches every file `build/check-release-version.sh` checks, together.
- The NuGet ID, App_Plugins folder, aliases, and any migration plan, table or route names are permanent once released.

## Behaviours to preserve

- The field type GUID `7c1e8b0a-3d52-4f6e-9a47-2b8d5c0e1f93` and alias `communityAddressLookup` never change: entries reference them.
- Never log a query, suggestion, address, provider id, IP or key, in logs, exception text or `ToString()`. `LeakTests` (sentinels in `src/Umbraco.Community.AddressLookup.Tests`) must stay green, including the Serilog-backed host case. The Ideal Postcodes client has its `HttpClient` loggers removed because its URI path and query carry data.
- Loqate calls are POST with a form body, so the URI carries nothing. Ideal Postcodes sends the key in the `Authorization` header, never the query string.
- Every proxy action is POST-only with an antiforgery token, a valid lookup ticket (the field is re-resolved each call, `Countries` comes from the field) and the throttle. Failure payloads are fixed.
- The proof makes `source:"lookup"` and is a data-quality control, not anti-spam. Capture mode never issues one.
- A server key never reaches a page: Capture refuses a key equal to either server key, trimmed.

## Commands

```bash
bash build/test.sh       # restore, build and test every line in eng/lines.txt (SQLite); or bash build/test.sh 17
dotnet restore src/Umbraco.Community.AddressLookup.slnx -p:UmbracoMajor=17 --locked-mode   # one line, locked
# SQL Server integration run: set Tests__Database__DatabaseType=SqlServer and Tests__Database__SQLServerMasterConnectionString
# Demo site: HTTPS only (OpenIddict refuses HTTP)
dotnet user-secrets set "Umbraco:CMS:Unattended:UnattendedUserPassword" "<pw>" --project demo/Umbraco.Community.AddressLookup.DemoSite
dotnet dev-certs https && dotnet run --project demo/Umbraco.Community.AddressLookup.DemoSite -p:UmbracoMajor=17   # https://localhost:44331/umbraco; use a separate database per line
# e2e (demo running; env vars as in tests/e2e/.env.example)
cd tests/e2e && npm ci --ignore-scripts && npx playwright install chromium && npx playwright test --reporter=line
bash build/check-vulnerable.sh
```

## Releasing

- Bump `PackageMinor`, `PackagePatch` and `PackageVersionSuffix` in `Directory.Build.props` (both lines move together), and rebuild the client if its source changed.
- `bash build/check-vulnerable.sh` and `bash build/check-release-version.sh release/<minor>.<patch>[-suffix]` both exit 0.
- Run the **Release checks** workflow (`release-checks.yml`) by hand on the release branch before every tag (SQL Server integration and e2e, including the Capture spec). Never on PRs. Re-run after a new Umbraco minor.
- Squash-merge, tag `release/<minor>.<patch>[-suffix]` on the merge commit; `publish.yml` (tag `release/*`, from `main` only) restores, builds, tests and audits both lines, packs them with `bash build/pack.sh` (one nupkg per line) and publishes with Trusted Publishing. Then `gh release create` per line, and mark the newest Umbraco line's release as Latest.
- Stable from the first real public release unless the API is still moving (`.github/REPO-SETUP.md` step 10); after that, no breaking changes within a major.
- "Done" means green on GitHub. The first CI run is its own step with Pascal.

## Keep

- Floors never below 17.8.0 / 18.3.0. The repo is public: no reviews, plans of record or local paths in it.
- `docs/` holds only `design.md`, `plan.md`, `spikes.md`. Fake test secrets carry `// gitleaks:allow` from their first commit.

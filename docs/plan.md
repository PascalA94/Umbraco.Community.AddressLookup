# Address Lookup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Execution is **native, plus one final whole-branch `qa-reviewer` review with a `security-analyst` pass** (Task 13). Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship `Umbraco.Community.AddressLookup` `17.0.0-alpha.1` and `18.0.0-alpha.1` from one commit: an Umbraco Forms "Address lookup" field with Loqate and Ideal Postcodes behind a server-side proxy, an optional Loqate Capture v4 mode, and a stub provider.

**Architecture:** A Razor class library. A composer registers one Forms field type, the `AddressLookup` options, one active `IAddressLookupProvider`, and a rate-limited, antiforgery-protected `SurfaceController` proxy. The field renders a combobox plus manual inputs and stores one JSON value, which `ValidateField` checks on submit.

**Tech Stack:** .NET 10, `Umbraco.Cms.Web.Website` `[17.8.0, 18.0.0)` / `[18.3.0, 19.0.0)`, `Umbraco.Forms.Core` + `Umbraco.Forms.Web` `[17.6.2, 18.0.0)` / `[18.2.2, 19.0.0)`, `System.Threading.RateLimiting`, ASP.NET Core Data Protection, vanilla ES modules (no client build), xUnit, NSubstitute, `FakeLogger`, Playwright + `@axe-core/playwright` (release-checks only), GitHub Actions, gitleaks, osv-scanner, CodeQL.

**Spec:** `docs/design.md`. Portfolio rules: the workspace README ("CI and GitHub rules" wins over this plan). Pitfalls: the Umbraco pitfalls reference.

**Repo:** the cloned repository root. `SRC` = `src/Umbraco.Community.AddressLookup`, `TESTS` = `tests/Umbraco.Community.AddressLookup.Tests`.

## Global Constraints

- **Both lines from the start.** `net10.0`, `eng/lines.txt` = `17` and `18`.
  - `UmbracoMajor` `17` gives CMS `[17.8.0, 18.0.0)` and Forms `[17.6.2, 18.0.0)`; `18` (the default) gives `[18.3.0, 19.0.0)` and `[18.2.2, 19.0.0)`.
  - `NuGetLockFilePath` = `packages.$(UmbracoMajor).lock.json`. Version `$(UmbracoMajor).$(PackageMinor).$(PackagePatch)`.
  - `scripts/test.sh` runs both lines. Expected once per line: `Passed!  - Failed:     0`.
- **Config section `AddressLookup`**, keys and defaults from spec 3.1 verbatim. `Provider` is `Loqate`, `IdealPostcodes` or `Stub`, case-insensitive. `Mode` is `Proxy` or `Capture`.
- **Field type:** GUID fixed at Task 9 and never changed. Alias `communityAddressLookup`, `DataType = LongString`.
- **Providers:** Loqate Find `v1.20` and Retrieve `v1.30` on `json6.ws`, called by **POST** with a form body and `IsMiddleware=true`. Ideal Postcodes `/v1/autocomplete/addresses` and `/{id}/gbr`.
- **Never log, or put in exception text, `ToString()`, HTML or a response:** an `ApiKey`, a query, a suggestion, an address part, a provider ID or a client IP.
- **Proxy:** POST only, `[ValidateAntiForgeryToken]`, a valid ticket, the throttle, `Cache-Control: no-store`. Failure payloads are fixed: `{error:"invalid"}` 400, 429 + `Retry-After`, `{error:"unavailable"}` 503.
- **Pitfalls:** P1/P2 [Critical] (singletons over `IHttpClientFactory`/`IOptionsMonitor`, no statics with request state), P5 [Critical] (no write services in the view), P7 (no network at start-up), S5 [Critical] (antiforgery).
- **CI (README rules, which override this plan):** the repo goes public before the first CI push; PRs run fast checks only (four-leg build/test, gitleaks over PR commits, osv-scanner `--config=./osv-scanner.toml`, check-vulnerable); e2e, SQL Server and Playwright only in `workflow_dispatch` `release-checks.yml`; random per-run passwords; scoped visible-only locators; e2e on HTTPS; `UmbracoUseAppLocalIcu=false`; SHA-pinned actions; Dependabot `open-pull-requests-limit: 0`; no NuGet cache in `publish.yml`; fake secrets carry `// gitleaks:allow`.
- **Claude never commits or pushes.** Each "Checkpoint (Pascal commits)" gives a suggested message with no AI attribution.

## Review Focus

1. **A visitor picks a result and then edits a line:** stored as `source:"manual"` and never rejected unless `RequireLookup` is on. Test: Task 9 `EditedAfterPick_StoredAsManual`.
2. **Non-ASCII and odd input** (`Ynys Môn`, `Rhosneigr`, an emoji, a 101-char line, a `\u0000`): accented text round-trips; controls and oversize are refused before any provider call or save. Test: Task 8 `Find_RejectsControlAndOversize` and Task 9 `Unicode_PartsRoundTrip`.
3. **Lower-case or unspaced postcodes** (`ls14ap`): accepted and normalised to `LS1 4AP`. Test: Task 3 `Postcode_Normalises` theory.
4. **A form page left open for over 2 h:** the expired ticket gives 400, the UI switches to manual entry with a message, and the submit still works. Test: Task 7 `Ticket_ExpiresAfterTwoHours` and Task 10 `expired ticket falls back to manual` (Playwright).
5. **The provider hangs:** the proxy returns 503 after `TimeoutSeconds`, and no request thread is held. Test: Task 5 `Timeout_ThrowsUnavailable` and Task 6 the same.

---

### Task 1: Clone and scaffold, public before CI

**Files:** `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `.gitignore`, `.gitattributes`, `LICENSE` (MIT, Pascal Meyer), `SECURITY.md`, `src/Umbraco.Community.AddressLookup.slnx`, `SRC/*.csproj` (`Microsoft.NET.Sdk.Razor`), `TESTS/*.csproj`, `TESTS/ScaffoldTests.cs`, `eng/lines.txt`, `scripts/{test,pack}.sh`, `osv-scanner.toml`, `.github/workflows/{build,release-checks}.yml`, `.github/dependabot.yml`, `docs/{design,plan,spikes}.md`, `demo/DemoSite.V17/`, `demo/DemoSite.V18/`

- [ ] **Step 1 (Pascal):** the repo `PascalA94/Umbraco.Community.AddressLookup` exists, private and empty. Before anything is pushed, run Step 8's pre-public checks, then **make it public** (README rule 1).
  - Set About: description, NuGet link, topics `umbraco`, `umbracopackage`, `dotnet`, `umbraco-forms`, `loqate`, `ideal-postcodes`.
  - Turn on secret scanning with push protection, CodeQL default setup, and squash-merge only.
- [x] **Step 2:** `gh repo clone PascalA94/Umbraco.Community.AddressLookup`. Confirm `git config user.email` prints `5427750+PascalA94@users.noreply.github.com`. Copy only `design.md` and `plan.md` into `docs/`, and start `docs/spikes.md`. Expected: `ls docs` shows `design.md plan.md spikes.md`.
- [x] **Step 3:** `Directory.Build.props` as in 15: `RestorePackagesWithLockFile=true`, `UmbracoMajor` (default `18`) setting `DefineConstants`, `UmbracoRange`, `FormsRange`, `NuGetLockFilePath` and the version prefix, plus `UmbracoFloat` for latest legs. The csproj references `Umbraco.Cms.Web.Website` at `$(UmbracoRange)`, and `Umbraco.Forms.Core` and `Umbraco.Forms.Web` at `$(FormsRange)`, with tags from spec 8.
- [x] **Step 4:** write `ScaffoldTests.Assembly_TargetsSelectedMajor` (asserts the `AssemblyMetadata("UmbracoMajor")` value and that the referenced `Umbraco.Forms.Core` major matches). Run `./scripts/test.sh`. Expected: FAIL. Add the metadata and re-run. Expected: `Passed!` twice, plus both lock files.
- [x] **Step 5:** demo sites `demo/DemoSite.V17` (`Umbraco.Templates::17.8.0`, Forms 17.6.2) and `demo/DemoSite.V18` (`18.3.0`, Forms 18.2.2), SQLite. Set `UmbracoUseAppLocalIcu=false` before the template conditions. Unattended admin `admin@example.test`, password in `dotnet user-secrets` only. Add a `ProjectReference` to `SRC` with `UmbracoMajor` set to match, and set `AddressLookup:Provider` to `Stub` in `appsettings.Development.json`.
- [x] **Step 6:** `build.yml`:
  - `permissions: {}`;
  - `build` job: matrix `line: [17, 18]` × `leg: [floor, latest]`;
  - `secrets` job: gitleaks CLI over the PR range, pinned tarball SHA-256;
  - `deps` job: osv-scanner and `build/check-vulnerable.sh`;
  - NuGet cache on.
- [x] **Step 7:** `release-checks.yml`, `workflow_dispatch` only. Each line's demo site boots on HTTPS (`dotnet dev-certs https`, `curl -sk` readiness) against SQL Server from `docker run`. Passwords come from `openssl rand -base64 24`, masked and passed through `$GITHUB_ENV`. It runs the Task 10 Playwright spec against the stub at `workers: 1`. No real provider calls in CI. Run `actionlint`. Expected: no output.
- [ ] **Step 8:** pre-public checks: `gitleaks dir . --redact` shows no leaks; `git grep -nE "ApiKey|CaptureKey" -- ':!*.md'` shows key names only; a grep for absolute home-directory paths prints nothing.
- [ ] **Checkpoint (Pascal commits):** `chore: scaffold both lines, demo sites and CI`. Make the repo public (Step 1), push a branch, open a PR, confirm the checks are green in about 5 minutes, then add the ruleset requiring `build`, `secrets` and `deps`.

### Task 2: Spikes S1 and S2 (record in `docs/spikes.md`)

**Files:** `docs/spikes.md`, throwaway code under `demo/DemoSite.V18/Spike/` (deleted at the end of the task)

- [x] **Step 1 (S1, field storage and views):** on the v18 demo site, add a throwaway `FieldType` with `DataType = LongString`, inputs named `{fieldId}.a` and `{fieldId}.b`, and a `ProcessSubmittedValue` that reads `context.Request.Form` and stores JSON. Record:
  - the call order of `ValidateField` and `ProcessSubmittedValue`;
  - whether a view in an RCL at `Views/Partials/Forms/Themes/default/FieldTypes/` is found, and whether a custom theme falls back to it;
  - how the stored value shows in the entries list, entry detail, CSV export and an email workflow placeholder;
  - whether `ConvertFromRecord` affects any of those.
- [x] **Step 2 (S2, preview element):** a hand-written `wwwroot/backoffice/preview.js` that imports `FormsFieldPreviewBaseElement` from `@umbraco-forms/backoffice`, registered by `umbraco-package.json` (`backofficeEntryPoint` plus a `formsFieldPreview` alias). Record whether the import map resolves it, and use a plain `UmbLitElement` as the fallback if not.
- [x] **Step 3:** repeat Steps 1 and 2 on the v17 demo site. Record any difference.
- [x] **Step 4:** write the Q1 outcome (one JSON value, plus `ConvertFromRecord` display only if S1 proves it is used) at the top of `docs/spikes.md`, then delete the spike code.
- [ ] **Checkpoint (Pascal commits):** `docs: S1 and S2 spike results`. **Pascal reviews the spikes before Task 3** (README build approach).

### Task 3: Options, composer and `AddressValue`

**Files:** `SRC/Configuration/{AddressLookupOptions,AddressLookupOptionsValidator,ProviderKind,LookupMode}.cs`, `SRC/Model/AddressValue.cs`, `SRC/AddressLookupComposer.cs`, `TESTS/{OptionsTests,AddressValueTests,ComposerTests}.cs`

**Interfaces:**
- Produces `AddressLookupOptions { ProviderKind? Provider; LookupMode Mode = Proxy; int MaxSuggestions = 8; int TimeoutSeconds = 5; RateLimitOptions RateLimits; LoqateOptions Loqate; IdealPostcodesOptions IdealPostcodes }`.
- `RateLimitOptions { int FindPerMinute = 30; int RetrievePerMinute = 6; int RetrievePerDay = 1000 }`.
- `LoqateOptions { string? ApiKey; string? CaptureKey; string CaptureScriptVersion = "4.05" }`, `IdealPostcodesOptions { string? ApiKey }`. Secrets are `[JsonIgnore]`, and `ToString()` prints `***`.
- `sealed record AddressValue(IReadOnlyList<string> Lines, string Town, string? County, string Postcode, string CountryIso2, string? Country, string? Provider, string? ProviderId, string Source)` with `string ToJson()`, `static bool TryParse(string json, out AddressValue? value)`, `AddressValue Normalise()`, `string Format()`, and the constant `int Version = 1` (JSON key `v`).

- [ ] **Step 1:** write the failing tests:
  - `OptionsTests`: `MissingLoqateKey_FailsNamingKey` (message has `AddressLookup:Loqate:ApiKey`, not the sentinel); `CaptureKeyEqualsApiKey_Fails` (exact spec 3.1 text); `CaptureWithIdealPostcodes_Fails`; `StubOutsideDevelopment_Fails`; `ToString_Redacts`;
  - `AddressValueTests`: `Json_RoundTrip_MatchesSpecShape` (the spec 3.3 example, byte for byte); `Postcode_Normalises` theory (`ls14ap` → `LS1 4AP`, `sw1a1aa` → `SW1A 1AA`); `Format_OneLine`; `TryParse_RejectsUnknownVersion`;
  - `ComposerTests`: `NoProvider_RegistersFieldTypeOnly`; `Stub_RegistersStubProvider` (a real `UmbracoBuilder`).
- [ ] **Step 2:** run them. Expected: FAIL.
- [ ] **Step 3:** implement. `Normalise` trims, collapses spaces, drops empty lines and upper-cases GB postcodes. The JSON uses camelCase `System.Text.Json` source-gen.
- [ ] **Step 4:** `./scripts/test.sh`. Expected: PASS on both lines.
- [ ] **Checkpoint (Pascal commits):** `feat: options, composer and address value`

### Task 4: Provider seam and stub

**Files:** `SRC/Providers/{IAddressLookupProvider,AddressLookupUnavailableException,StubProvider}.cs`, `SRC/Model/{AddressQuery,AddressSuggestion}.cs`, `TESTS/StubProviderTests.cs`

**Interfaces:** Produces the spec 4.2 interface and records verbatim, plus `AddressLookupUnavailableException(string provider, string code)` with `InnerException` always null.

- [ ] **Step 1:** write the failing tests: `Find_ST1_ReturnsContainerAndAddresses`; `Find_Container_DrillsIn`; `Retrieve_KnownId_ReturnsAddress`; `Retrieve_UnknownId_Null`; `Find_UNAVAILABLE_Throws` (the magic text `ZZ99 9ZZ` throws, so the fallback UI can be tested).
- [ ] **Step 2:** run them. Expected: FAIL.
- [ ] **Step 3:** implement a fixed, fictional data set (no real addresses).
- [ ] **Step 4:** run them. Expected: PASS.
- [ ] **Checkpoint (Pascal commits):** `feat: provider seam and stub`

### Task 5: Loqate provider

**Files:** `SRC/Providers/Loqate/{LoqateProvider,LoqateDtos}.cs`, `TESTS/LoqateProviderTests.cs`

**Interfaces:** `LoqateProvider : IAddressLookupProvider`, alias `loqate`, named client `community.addressLookup.loqate`, base `https://api.addressy.com/`, `AllowAutoRedirect = false`.

- [ ] **Step 1:** write the failing tests, using a recording handler:
  - `Find_PostsFormBody` (`POST Capture/Interactive/Find/v1.20/json6.ws`; body has `Key`, `Text`, `Countries`, `Limit`, `IsMiddleware=true`, `Container` only when set; the URI carries no query string);
  - `Find_MapsTypeToIsContainer` (`Address` → false; `Postcode`, `Street`, `Container` → true);
  - `Retrieve_MapsFields` (Company first, `Line1-3`, `City`, `Province`, `PostalCode`, `CountryIso2`, `CountryName`);
  - `Errors_MapToUnavailable` theory (codes 2, 3, 4, 8, 10, 14, 17, -1 and 1001 give the exception with that code);
  - `Errors_LogThrottled` (two identical errors in a minute give one Warning line);
  - `Timeout_ThrowsUnavailable`; `Redirect_NotFollowed`.
- [ ] **Step 2:** run them. Expected: FAIL.
- [ ] **Step 3:** implement. Use `ResponseHeadersRead`, read at most 64 KB, and log only `provider` and `code`.
- [ ] **Step 4:** run them. Expected: PASS. Record the error-item JSON shape seen in the docs in `docs/spikes.md` (spec 0 [unverified]).
- [ ] **Checkpoint (Pascal commits):** `feat: Loqate provider`

### Task 6: Ideal Postcodes provider

**Files:** `SRC/Providers/IdealPostcodes/{IdealPostcodesProvider,IdealPostcodesDtos}.cs`, `TESTS/IdealPostcodesProviderTests.cs`

**Interfaces:** `IdealPostcodesProvider : IAddressLookupProvider`, alias `idealPostcodes`, named client `community.addressLookup.idealPostcodes`, base `https://api.ideal-postcodes.co.uk/`.

- [ ] **Step 1:** write the failing tests:
  - `Find_CallsAutocomplete` (`GET v1/autocomplete/addresses` with `query`, `limit`, `api_key`; every hit has `IsContainer=false`);
  - `Retrieve_CallsResolveGbr` (`v1/autocomplete/addresses/{id}/gbr`, mapping `line_1-3`, `post_town`, `county`, `postcode`, `organisation_name` first; country `GB`);
  - `Errors_MapToUnavailable` theory (4010, 4011, 4020, 4021, 4291, 5001; 4048 gives null);
  - `Timeout_ThrowsUnavailable`;
  - `NoSourceIpHeader` (`IDPC-Source-IP` is never sent).
- [ ] **Step 2:** run them. Expected: FAIL.
- [ ] **Step 3:** implement. Remove the `System.Net.Http.HttpClient.community.addressLookup.*` log categories in the composer (`AddFilter(..., LogLevel.None)`), because the GET URI carries the key.
- [ ] **Step 4:** run them. Expected: PASS.
- [ ] **Checkpoint (Pascal commits):** `feat: Ideal Postcodes provider`

### Task 7: Ticket, proof and rate limiter

**Files:** `SRC/Security/{LookupTicketService,AddressProofService,LookupRateLimiter}.cs`, `TESTS/{TicketTests,ProofTests,RateLimiterTests}.cs`

**Interfaces:**
- `LookupTicketService.Issue(Guid formId, Guid fieldId) : string` and `TryRead(string ticket, out LookupTicket t) : bool`, where `LookupTicket(Guid FormId, Guid FieldId, DateTimeOffset Issued)`; purpose `community.addressLookup.ticket.v1`, lifetime 2 h.
- `AddressProofService.Issue(AddressValue v, Guid fieldId) : string` and `Verify(string proof, AddressValue posted, Guid fieldId) : bool`; purpose `community.addressLookup.proof.v1`, lifetime 2 h.
- `LookupRateLimiter.TryAcquire(LookupKind kind, string partitionKey) : RateLimitDecision(bool Allowed, TimeSpan? RetryAfter, bool DailyCapHit)`, where `partitionKey` = SHA-256 of ticket plus IP.
- All take `TimeProvider`.

- [ ] **Step 1:** write the failing tests:
  - `Ticket_RoundTrip`, `Ticket_ExpiresAfterTwoHours`, `Ticket_Tampered_False`;
  - `Proof_MatchesNormalisedValue` (the same address with extra spaces verifies), `Proof_EditedLine_False`, `Proof_OtherField_False`;
  - `Find_31stInMinute_Denied`, `Retrieve_7thInMinute_Denied`, `RetrievePerDay_GlobalCap` (the 1001st across partitions is denied with `DailyCapHit`), `DailyCap_ResetsAtUtcMidnight`;
  - `PartitionKey_IsHashed` (no raw IP is stored).
- [ ] **Step 2:** run them. Expected: FAIL.
- [ ] **Step 3:** implement with `IDataProtectionProvider.CreateProtector(purpose).ToTimeLimitedDataProtector()` and a `PartitionedRateLimiter<string>` of sliding windows, plus an `Interlocked` daily counter.
- [ ] **Step 4:** run them. Expected: PASS.
- [ ] **Checkpoint (Pascal commits):** `feat: lookup ticket, proof and throttle`

### Task 8: Proxy controller

**Files:** `SRC/Controllers/AddressLookupController.cs`, `TESTS/AddressLookupControllerTests.cs`

**Interfaces:** Consumes Tasks 4-7. Produces `POST /umbraco/surface/AddressLookup/Find` (`ticket`, `text`, `container?`) returning `[{id,text,description,isContainer}]`, and `POST .../Retrieve` (`ticket`, `id`) returning `{address, proof}`.

- [ ] **Step 1:** write the failing tests:
  - `NoAntiforgery_400` (an attribute test proving `[ValidateAntiForgeryToken]` and `[HttpPost]` are on both actions);
  - `BadTicket_400_ProviderNotCalled`; `FieldNotOnForm_400`;
  - `Find_RejectsControlAndOversize` theory (`"a"`, 101 chars, `"\u0000ab"`, emoji-only);
  - `Find_UsesServerCountries` (a posted `countries` is ignored);
  - `Throttled_429_RetryAfter`; `DailyCap_503_OneWarningPerDay`; `Unavailable_503_FixedBody`; `Responses_NoStore`; `Find_CapsAtMaxSuggestions`.
- [ ] **Step 2:** run them. Expected: FAIL.
- [ ] **Step 3:** implement. Look up the field through `IFormService.Get(formId)` (read-only; cache per form id for 1 min in `AppCaches.RuntimeCache`). Add a why-comment linking spec 5.2.
- [ ] **Step 4:** run them. Expected: PASS.
- [ ] **Checkpoint (Pascal commits):** `feat: rate-limited lookup proxy`

### Task 9: Field type, storage and submit validation

**Files:** `SRC/FieldTypes/AddressLookupFieldType.cs`, `SRC/FieldTypes/AddressFormReader.cs`, `TESTS/FieldTypeTests.cs`

**Interfaces:**
- `AddressLookupFieldType : FieldType` with `public static readonly Guid TypeId` (new GUID, fixed here forever), `Alias = "communityAddressLookup"`, `FieldTypeViewName = "FieldType.AddressLookup.cshtml"`, `PreviewView = "Community.AddressLookup.Preview"`.
- Settings from spec 3.2. Overrides `ProcessSubmittedValue`, `ValidateField` (6-arg), `GetConfigurationErrors` and `ConvertFromRecord` (only if S1 says so).
- `AddressFormReader.Read(IFormCollection form, Guid fieldId) : (AddressValue? value, string? proof)`.

- [ ] **Step 1:** write the failing tests over a fake `HttpContext.Request.Form`:
  - `Lookup_WithValidProof_StoresSourceLookup`; `EditedAfterPick_StoredAsManual`;
  - `TamperedProof_RequireLookup_Error` ("Select an address from the list");
  - `Mandatory_MissingPostcode_Error` ("Enter a postcode"); `Mandatory_MissingLine1_Error` ("Enter the first line of the address"); `Mandatory_MissingTown_Error` ("Enter a town or city");
  - `BadGbPostcode_Error` ("Enter a real postcode"); `CountryNotAllowed_Error` ("Choose a country from the list");
  - `OversizePart_Error`; `ControlChars_Error`; `Unicode_PartsRoundTrip` (`Ynys Môn`); `Optional_AllEmpty_StoresNothing`;
  - `ConfigErrors_IdealPostcodesNonGb`, `ConfigErrors_RequireLookupInCapture`.
- [ ] **Step 2:** run them. Expected: FAIL.
- [ ] **Step 3:** implement. Error copy is fixed strings only. Register with `builder.WithCollectionBuilder<FieldCollectionBuilder>().Add<AddressLookupFieldType>()`.
- [ ] **Step 4:** `./scripts/test.sh`. Expected: PASS on both lines.
- [ ] **Checkpoint (Pascal commits):** `feat: address lookup field type and submit validation`

### Task 10: View, combobox script, Capture mode and preview

**Files:** `SRC/Views/Partials/Forms/Themes/default/FieldTypes/FieldType.AddressLookup.cshtml`, `SRC/wwwroot/{address-lookup.js,address-lookup.css}`, `SRC/wwwroot/backoffice/preview.js`, `SRC/umbraco-package.json`, `e2e/address-lookup.spec.ts`, `e2e/playwright.config.ts`

- [ ] **Step 1:** write the failing Playwright spec against the v18 demo site with the stub (local, then `release-checks.yml`):
  - `keyboard pick fills fields` (type `ST1`, ArrowDown ×2, Enter; line 1 is focused and filled);
  - `container drills in and announces count` (the live region contains "addresses in");
  - `escape closes then clears`; `tab leaves without picking`;
  - `manual entry button works`; `no-JS shows manual inputs` (`javaScriptEnabled: false`);
  - `unavailable falls back to manual` (`ZZ99 9ZZ`); `expired ticket falls back to manual` (route the POST to return 400);
  - `axe has no violations` (`@axe-core/playwright`, tags `wcag2a`, `wcag2aa`, `wcag21aa`, `wcag22aa`), open and closed;
  - `capture mode loads v4 script without ApiKey` (a second config; assert the `api.addressy.com/js/address-4.05.js` request and that the HTML has no sentinel `ApiKey`).
  - Locators are scoped and visible-only (README rule 11).
- [ ] **Step 2:** run `npx playwright test`. Expected: FAIL.
- [ ] **Step 3:** implement:
  - the view: label, the combobox markup from spec 6, manual inputs with `autocomplete` tokens, hidden `.providerId` and `.proof`, the ticket from `LookupTicketService.Issue` and the antiforgery token in `data-` attributes, injecting no write service (P5);
  - the script: a vanilla ES module with a 250 ms debounce, a 3-char minimum, `AbortController` per request and the ARIA state machine;
  - Capture mode in the same view behind `Mode`;
  - the preview element from the S2 outcome.
- [ ] **Step 4:** run the spec on both demo sites. Expected: all pass. Also run a VoiceOver smoke (Safari, macOS): the field label, the option count and the selection are announced. Record the result in `docs/spikes.md`.
- [ ] **Checkpoint (Pascal commits):** `feat: accessible lookup UI, Capture mode and backoffice preview`

### Task 11: Leak tests, docs and release workflow

**Files:** `TESTS/LeakTests.cs`, `README.md`, `CLAUDE.md`, `umbraco-marketplace.json`, `.github/workflows/publish.yml`

- [ ] **Step 1:** write `LeakTests.NoSecretsOrPersonalData` theory over every failure path in Tasks 3-9. The sentinels `lq-SENTINEL-KEY` and `ip-SENTINEL-KEY` (`// gitleaks:allow`), query `SENTINEL QUERY` and line `1 Sentinel Street` must never appear in `FakeLogger` records, exception messages, `ToString()`, controller responses or rendered view HTML. Run. Expected: PASS (fix under the owning task if not).
- [ ] **Step 2:** README:
  - install and a supported-versions table (`17.x`: CMS 17.8+ and Forms 17.6.2+; `18.x`: CMS 18.3+ and Forms 18.2.2+);
  - the config table (spec 3.1), the env-var form for secrets, and key restrictions (spec 5.4);
  - the modes, with Capture's limits and CSP;
  - the stored JSON shape;
  - the retention and processor note (spec 5.1), and per-instance limits;
  - **"Migrating from a hand-built field"** (spec 8, verbatim mapping);
  - uninstall.
- [ ] **Step 3:** `CLAUDE.md`, covering behaviours to preserve: the fixed GUID, the never-log list, POST-only to Loqate, the ticket, proof and throttle, the floors, and the release checklist (run `release-checks.yml` before every tag).
- [ ] **Step 4:** `umbraco-marketplace.json` with tags from spec 8. `publish.yml`: on a `release/*` tag, `scripts/pack.sh` (one nupkg per line), trusted publishing, `id-token: write` only there, no NuGet cache. Run `actionlint`. Expected: no output.
- [ ] **Step 5 (Pascal):** reserve the NuGet ID (`0.0.1-alpha`, push, unlist, 2FA) and add a Trusted Publishing policy.
- [ ] **Checkpoint (Pascal commits):** `docs: README, migration guide and release workflow`

### Task 12: First full GitHub CI run (Pascal, with the agent fixing)

- [ ] **Step 1 (agent):** pre-push checks:
  - `./scripts/test.sh` passes on both lines; `gitleaks dir . --redact` is clean;
  - `ls docs` shows only `design.md plan.md spikes.md`;
  - `git log --all --name-only --format= | grep -iE "scope\.md|review|council"` prints nothing;
  - `grep -rn "uses: " .github/workflows | grep -vE "@[0-9a-f]{40}"` prints nothing.
  - State plainly in the handover: **CI has not run yet**.
- [ ] **Step 2 (Pascal):** push and open a PR. Expected: `build` (four legs), `secrets` and `deps` are green in about 5 minutes.
- [ ] **Step 3 (Pascal):** `gh workflow run release-checks.yml --ref <branch>`. Expected: both lines green (HTTPS boot, SQL Server, the Playwright and axe spec).
- [ ] **Step 4 (agent):** fix each failure under the owning task's tests, one round at a time. Record local-vs-GitHub differences in `docs/spikes.md`.
- [ ] **Checkpoint (Pascal commits):** `ci: fixes from the first GitHub run` (only if anything changed).

### Task 13: Final whole-branch review, with a security-analyst pass

- [ ] **Step 1:** dispatch `qa-reviewer` (fresh context, model `opus`) with the branch diff, `docs/*` and `~/.claude/references/umbraco-pitfalls.md`. Ask for findings by severity against Global Constraints, Review Focus and spec 3.5.
- [ ] **Step 2:** dispatch `security-analyst` (fresh context, model `opus`, in parallel) for spec 5: an open-relay and credit-burn attempt (no ticket, a replayed ticket, IP rotation against the daily cap), CSRF, key exposure in Capture and proxy modes, log and personal-data leaks, proof forgery, SSRF (outbound hosts fixed), and the dependency audit. **Critical findings can't be waived.**
- [ ] **Step 3:** save both verdicts to the private workspace reviews folder (never in the repo). Fix blocking findings under the owning task's tests, then run `./scripts/test.sh`. Expected: `Failed: 0`, CI green.
- [ ] **Checkpoint (Pascal commits):** `fix: final review findings`

### Task 14: Exercise it for real, on 17 and 18, then release

- [ ] **Step 1 (Pascal):** keys go in `dotnet user-secrets` on the demo sites, never in chat or the repo: the Ideal Postcodes test key `iddqd` (costs no lookups), and a **Loqate trial key if Pascal provides one**, IP-restricted, with a second URL-restricted key for Capture.
- [ ] **Step 2:** on **both** demo sites (17.8 + Forms 17.6.2, 18.3 + Forms 18.2.2):
  - **Stub:** add the field to a form with an email workflow; submit lookup, manual and no-JS entries. Expected: each entry is stored with the right `source`, and entries, export and email show what S1 recorded.
  - **Ideal Postcodes:** search `ID1 1QD` and pick a result, which stores `source:"lookup"`. `ID1 CLIP` gives manual fallback, with a log line showing `idealPostcodes` and `4020` only.
  - **Loqate (if a key):** search a real postcode, drill into a container and pick a result. Then switch to `Mode: Capture` and pick a result. Expected: the v4 script loads and the page source has no server key.
- [ ] **Step 3:** negatives: 31 quick Finds give 429 in the UI with manual fallback; a wrong key gives 503 with no key in the logs; a tampered `.proof` (devtools) with `RequireLookup` on gives "Select an address from the list".
- [ ] **Step 4:** `grep -rciE "lq-|iddqd|ID1 1QD|ST1 1AA|<a real searched postcode>" demo/**/umbraco/Logs` prints only zeros.
- [ ] **Step 5 (Pascal):** run `release-checks.yml`, then push the tag `release/0.0-alpha.1`. Expected: `17.0.0-alpha.1` and `18.0.0-alpha.1` on NuGet from one commit. Run `gh release create --prerelease --generate-notes`. The Marketplace listing goes on at `17.0.0` and `18.0.0`.
- [ ] **Checkpoint (Pascal commits):** `docs: acceptance results`


## Resolved by Pascal (2026-11-04)

- **Stored value:** one JSON value. Show it readably in entries only if spike S1 (Task 2) finds a display hook.
- **Proxy rate limits:** 30 Finds and 6 Retrieves per minute, and 1,000 Retrieves per day, all configurable.
- **Ideal Postcodes in browser mode:** not in 1.0. Browser (Capture) mode is Loqate only; Ideal Postcodes goes through the server proxy.

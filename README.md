# Address Lookup

An Umbraco Forms address lookup field with Loqate and Ideal Postcodes behind a server-side proxy.

[![Build](https://github.com/PascalA94/Umbraco.Community.AddressLookup/actions/workflows/build.yml/badge.svg)](https://github.com/PascalA94/Umbraco.Community.AddressLookup/actions/workflows/build.yml)
[![NuGet](https://img.shields.io/nuget/v/Umbraco.Community.AddressLookup)](https://www.nuget.org/packages/Umbraco.Community.AddressLookup)
[![NuGet downloads](https://img.shields.io/nuget/dt/Umbraco.Community.AddressLookup)](https://www.nuget.org/packages/Umbraco.Community.AddressLookup)
[![Licence: MIT](https://img.shields.io/badge/licence-MIT-blue)](https://github.com/PascalA94/Umbraco.Community.AddressLookup/blob/main/LICENSE)

A community package, not made or supported by Umbraco HQ.

<!-- TODO before the first release: add the best screenshot from assets/screenshots/ with an absolute URL, so it also shows on NuGet:
![What it looks like](https://raw.githubusercontent.com/PascalA94/Umbraco.Community.AddressLookup/main/assets/screenshots/screenshot-01-<what>.png) -->

A visitor types a postcode or part of an address, picks a result and gets a structured address. They can always type it in by hand, with or without JavaScript. The field stores one JSON value per entry, so a half-filled address cannot go missing.

By default the browser only talks to your site. Your site talks to the provider with a key that never leaves the server. An optional Capture mode loads Loqate's own browser widget instead, with a separate browser key.

## Compatibility

| Package version | Umbraco CMS | Umbraco Forms | .NET |
|---|---|---|---|
| 17.x | 17.8.0 or later, below 18 | 17.6.2 or later, below 18 | 10 |
| 18.x | 18.3.0 or later, below 19 | 18.2.2 or later, below 19 | 10 |

Pick the package line that matches your CMS major. Both lines come from the same code and the same tag.

## Install

Umbraco 18:

This is a prerelease, so the commands need `--prerelease` (or a pinned version) until 17.0.0 and 18.0.0 are out.

```bash
dotnet add package Umbraco.Community.AddressLookup --prerelease
```

Umbraco 17 (the unpinned command above installs the latest 18.x, so pin the 17 line):

```bash
dotnet add package Umbraco.Community.AddressLookup --version 17.0.0-alpha.1
```

Use the latest 17.x version number from NuGet in place of `17.0.0-alpha.1`.

Then configure a provider (below) and add an "Address lookup" field to a form in the Forms designer. With no provider set, the field still works as plain manual entry.

## Configuration

Everything lives in the `AddressLookup` section of `appsettings.json`. Provider, mode and rate limits are read once, so changing `Provider`, `Mode` or `RateLimits` needs a restart.

```json
"AddressLookup": {
  "Provider": "Loqate",
  "Mode": "Proxy",
  "MaxSuggestions": 8,
  "TimeoutSeconds": 5,
  "RateLimits": {
    "FindPerMinute": 30,
    "RetrievePerMinute": 6,
    "RetrievePerDay": 1000,
    "FindPerMinutePerIp": 60,
    "RetrievePerMinutePerIp": 12,
    "FindPerDay": 20000
  },
  "Loqate": { "CaptureScriptVersion": "4.05" },
  "IdealPostcodes": { }
}
```

| Key | Default | Meaning |
|---|---|---|
| `Provider` | unset | `Loqate`, `IdealPostcodes` or `Stub`. Unset means manual entry only. `Stub` is for development and is a start-up error in any other environment. |
| `Mode` | `Proxy` | `Proxy` or `Capture`. Capture needs Loqate. |
| `MaxSuggestions` | 8 | 1 to 20 results per search. |
| `TimeoutSeconds` | 5 | 1 to 30. A provider that does not answer in time counts as unavailable. |
| `RateLimits:FindPerMinute` | 30 | Searches per form page (per ticket) per minute. |
| `RateLimits:RetrievePerMinute` | 6 | Picks per form page per minute. |
| `RateLimits:RetrievePerDay` | 1000 | Picks across the whole site per UTC day. Retrieve is the paid call on both providers, so this bounds spend. |
| `RateLimits:FindPerMinutePerIp` | 60 | Searches per client IP per minute, across all pages. |
| `RateLimits:RetrievePerMinutePerIp` | 12 | Picks per client IP per minute. |
| `RateLimits:FindPerDay` | 20000 | Searches across the whole site per UTC day. |
| `Loqate:ApiKey` | none | Server key. Required when `Provider` is `Loqate` and `Mode` is `Proxy`. |
| `Loqate:CaptureKey` | none | Browser key. Required when `Mode` is `Capture`, and used for nothing else. Must differ from the server key. |
| `Loqate:CaptureScriptVersion` | `4.05` | Digits and dots only, otherwise the default is used. |
| `IdealPostcodes:ApiKey` | none | Server key. Required when `Provider` is `IdealPostcodes`. |

Limits are per instance. On a load-balanced site each instance counts for itself, so also set the daily limit on the provider's own key.

### Keys

Keys come from environment variables or a secret store. Never commit one.

```bash
AddressLookup__Loqate__ApiKey=<server key>
AddressLookup__Loqate__CaptureKey=<browser key>
AddressLookup__IdealPostcodes__ApiKey=<server key>
```

Start-up validation names the missing configuration key and never prints a value.

- Loqate: give the server key an **IP restriction** and a daily limit in the Loqate dashboard. Both are supported.
- Ideal Postcodes: set a usage limit on the key and use any restriction the dashboard offers. The exact controls available for a server-side key are unverified until a live check.
- Loqate Capture key: give it a **URL restriction**. It is visible in the page source by design.
- Use separate keys for each environment.
- A Capture key that matches the server key (ignoring surrounding whitespace) is refused at start-up and never rendered.

### Per-field settings

Set these on the field in the Forms designer.

| Setting | Default | Meaning |
|---|---|---|
| Countries | `GB` | Two-letter ISO codes, comma separated. The server takes this from the field, never from the request. Ideal Postcodes covers Great Britain only. |
| Placeholder | empty | Placeholder for the search box. |
| Allow manual entry | on | Shows the "Enter address manually" button. |
| Require lookup | off | Refuses a typed address and asks the visitor to pick from the list. Proxy mode only. |
| Show country | off | Shows the country input. When off, the first of Countries is used. |

Require lookup with no provider is ignored at submit, and Capture mode cannot use it. The Development diagnostics list flags both.

## Modes

**Proxy (default).** The page carries a short-lived ticket tied to the form and field. The browser posts searches to `/umbraco/surface/AddressLookup/Find` and picks to `.../Retrieve`, both with an antiforgery token. The site calls the provider and returns only ids and text, then the picked address with a proof. A visitor who picks a result and then edits it is stored as `manual`. Every failure shows a message and falls back to manual entry.

**Capture (Loqate only).** The page loads Loqate's Capture v4 script and fills the same inputs, using `CaptureKey`.

- The widget sends every keystroke and the visitor's IP address to Loqate.
- The script comes from `api.addressy.com` with no Subresource Integrity, so you trust that host to serve it.
- The widget may set cookies or use browser storage. This is unverified: check with a trial key, and check your consent banner covers it.
- A site with a Content Security Policy must allow `api.addressy.com` for scripts and styles, plus whatever hosts the widget connects to. Check this with a trial key, because the widget's own requests are outside this package.
- The mapping from Capture's v4 fields to the inputs has not been checked against live Loqate responses.
- Capture's accessibility is Loqate's, not this package's. Use Proxy mode where WCAG 2.2 AA is a contractual requirement.
- Results cannot be verified server-side, so the stored `source` is always `manual`.

### What the proof does

When a visitor picks a result, the site signs it (ASP.NET Core Data Protection) so a later submit can tell a picked address from a typed one without a second paid call. The proof is a data-quality control, not anti-spam: it is replayable by anyone for two hours on the same field. Do not use Require lookup to keep bots out.

## Stored value

One compact JSON value per entry:

```json
{"v":1,"lines":["Flat 2","10 High Street"],"town":"Leeds","county":null,"postcode":"LS1 4AP",
 "countryIso2":"GB","country":"United Kingdom","provider":"loqate","providerId":"GB|RM|A|123","source":"lookup","t":"CfDJ8..."}
```

- `source` is `lookup` only when the proof verifies. Otherwise it is `manual` and `provider` and `providerId` are null.
- `t` is a tag the server adds to a verified lookup value, so a stored value can be shown again (multi-page forms, a failed submit) without being re-checked by the provider. It is protected with ASP.NET Data Protection, so hosts behind a load balancer need a shared key ring, as for the proof. A lookup value without a valid tag is treated as `manual`. Ignore the field in your own reading code.
- Headless clients (the Forms Delivery API) go through the same checks. With `RequireLookup` on, a headless post is accepted only when it carries a value the server tagged.
- Forms shows this raw JSON in the entries view, in the CSV export and in the default email. Forms has no hook for a readable form, so use a workflow that formats the value, or read the JSON in your own code. `AddressValue.Format()` gives a one-line form.
- Submit-time checks: line 1, town and postcode are required once anything is typed. Parts are length-capped, control characters, `<` and `>` and spreadsheet-formula starts are refused, a GB postcode is checked against the standard pattern, and the country must be in the field's Countries.
- Commercial addresses can return four lines. The field holds three, so extra lines are folded into line 3.
- The country name comes from the .NET `RegionInfo` English name. It varies with ICU data and is empty under `InvariantGlobalization`. `countryIso2` is always set.

## Privacy and retention

- An address is personal data. Entries hold addresses, so set Forms' record retention to match your privacy notice.
- Loqate and Ideal Postcodes act as your processors for what visitors type, under their data processing terms. Accept their DPA, list them in your privacy notice and check where they process data.
- The package's own code never logs a search, a suggestion, an address, a provider id or an IP address, and it removes the `HttpClient` logging handlers from its provider clients. The throttle keys on a hash with a random per-instance key.
- Loqate calls are POSTs with a form body, so the URL carries nothing. Ideal Postcodes calls are GETs: the URL carries the search text (Find) and the provider id (Retrieve). A host that adds its own `HttpClient` logging, or an APM tool that records outgoing URLs, can still capture them, so check what yours records for `api.ideal-postcodes.co.uk`.
- As a safeguard, keep `System.Net.Http.HttpClient.community.addressLookup` at Warning or above, for example with a Serilog `MinimumLevel.Override`.
- `IDPC-Source-IP` is not sent, because it would share visitor IPs with Ideal Postcodes.

## Running it in production

- **Behind a CDN or load balancer**, configure `ForwardedHeadersOptions` with `KnownProxies` or `KnownNetworks` set (never cleared) and a matching `ForwardLimit`. Without that the per-IP throttle sees the edge address, so one visitor can use up the per-IP budget for everyone, and loose settings let a client spoof its own address.
- **Data Protection.** Tickets and proofs use ASP.NET Core Data Protection. On more than one instance, or on ephemeral storage, persist and share the key ring and protect it at rest. Otherwise tickets and proofs fail after a restart or on another instance.
- **A CDN or full-page cache in front of form pages** hands every visitor the same ticket and antiforgery token. Exclude pages with the field from the cache, or lookups will fail or share one budget.
- **Sites that IP-lock `/umbraco/*`** will block the proxy, which lives under `/umbraco/surface/`. Allow POST to `/umbraco/surface/AddressLookup/Find` and `/umbraco/surface/AddressLookup/Retrieve` only.
- **When a daily cap is reached** (`RetrievePerDay` or `FindPerDay`), visitors get the unavailable message and fall back to manual entry until UTC midnight. The site logs one Warning a day. A bot behind rotating IPs can use up the caps, so set the provider's own limits as well.
- **Without JavaScript** the manual inputs show and a stylesheet in a `<noscript>` block hides the search box. A strict CSP that blocks that stylesheet leaves the search box showing with nothing behind it.
- **The diagnostics list** of configuration problems shows only in the Development environment or to a signed-in backoffice user. The backoffice-user path is unverified.

## Forms behaviour to know about

- **Conditions.** A field hidden by its own condition, or by its fieldset's, does not block the submit and stores nothing, whether it is mandatory or not. Forms has no page-level condition, so pages need nothing.
- **Multi-page forms.** Going Back refills the address from Forms' stored value, and a stored lookup keeps its verification. Only a two-page demo form has been tried, so test other layouts on your own form.
- **Manual-only mode** (no provider). Forms' caption label points at nothing, so clicking it focuses no input. The Forms designer preview and the settings views were checked on Umbraco 17 only.

## Migrating from a hand-built field

Old entries are not converted. Add the new field beside the old one, switch the form, then remove the old field.

| You have | Move it to |
|---|---|
| `LoqateSettings:Key` (browser key, Capture v3) | `AddressLookup:Loqate:CaptureKey`, with `Mode` set to `Capture` |
| `Loqate:LoqateApiKey` (Capture v4) | `AddressLookup:Loqate:CaptureKey`. For proxy mode use `AddressLookup:Loqate:ApiKey` and give the key a new IP restriction |
| `Forms:AddressLookup:Provider` and `ApiKey` | `AddressLookup:Provider` and `AddressLookup:Loqate:ApiKey` |
| Ideal Postcodes key in a `data-sitekey` attribute | `AddressLookup:IdealPostcodes:ApiKey` in proxy mode, then stop using the key in the browser |

1. Add the "Address lookup" field to the form next to the old one and test a submit.
2. Remove the old field from the form.
3. Keep the old field type class registered while you still need to read old entries, because entries reference its GUID. This is unverified: check it on a copy first.
4. A key that was ever in a page is exposed. Rotate it when you move to proxy mode.

## Uninstall

1. Remove the Address lookup field from every form.
2. Remove the package: `dotnet remove package Umbraco.Community.AddressLookup`.
3. Remove the `AddressLookup` configuration section and its keys, and revoke the provider keys you no longer use.
4. Existing entries keep their JSON. The field type id is permanent, `7c1e8b0a-3d52-4f6e-9a47-2b8d5c0e1f93`. Forms' handling of entries whose field type is no longer registered is unverified, so export what you need first.

## Develop

See `CLAUDE.md` for the commands. In short: `bash build/test.sh` restores, builds and tests both Umbraco lines; run the demo site under `demo/`.

## Licence

MIT. See `LICENSE`. Security issues: see `SECURITY.md`.

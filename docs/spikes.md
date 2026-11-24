# Spikes

Results of the M0 spikes. Run on 2026-11-16 against CMS 18.3.0 with Forms 18.2.2 and CMS 17.8.0 with Forms 17.6.2 (the floors), on the demo site with SQLite. The throwaway code is deleted.

Tags: **[run]** observed on a running demo site, **[static]** read from the decompiled Forms assemblies, **[not run]** not verified yet.

## Outcome (Q1)

**One JSON value, no display hook.** `ConvertFromRecord` does not drive any display, so the stored JSON is what an editor sees in the entries list, an exported CSV and an email. Ship the JSON and document it in the README.

- Entries list, CSV export (both types) and the default "Send email" body show the raw JSON string [run, 17 and 18].
- A `{alias}` placeholder in a workflow field (for example a subject) resolves to the raw JSON too [run].
- `ConvertFromRecord` is only called for workflow form state and for re-rendering a form from a record [static: `RecordExtensions.CreateFormStateFromRecord`, called by `WorkflowExecutionService` and `FormRenderingService`; run: it fires twice after each submit].
- The field's `EditView` (a backoffice property editor UI alias, `Umb.PropertyEditorUi.Label` by default) decides how the value shows in the entry detail [static, API schema]. A custom edit view is the only display hook left, and it is outside 1.0.

## S1: field storage and views

**Call order on submit** [run, identical on 17 and 18]:

1. `ProcessSubmittedValue` (our inputs are on `HttpContext.Request.Form`; `postedValues` is empty)
2. `ConvertToRecord`
3. `ValidateField` (`postedValues` is empty here too)
4. `ConvertToRecord` (again)
5. `ConvertFromRecord` twice (workflow execution), each followed by `ConvertToRecord`
6. the record is saved

Consequences for Task 9:

- Forms only passes `Request.Form[fieldId]` as the posted value. Inputs named `{fieldId}.line1` and so on never reach the `postedValues` arguments, so both `ProcessSubmittedValue` and `ValidateField` must read `context.Request.Form` themselves. `AddressFormReader` does that.
- `ProcessSubmittedValue` runs before `ValidateField`, and its return value goes through `ConvertToRecord`. The stored value is the string `ProcessSubmittedValue` returns.
- `System.Text.Json` escapes non-ASCII by default (`Môn` is stored as `Môn`). That is valid JSON and round-trips, but it is hard to read in a CSV. Use a relaxed encoder in `AddressValue.ToJson()` and test `Unicode_PartsRoundTrip` on the stored string.

**Views in a Razor class library** [run, 17 and 18]:

- The field view is found when it is shipped in the RCL, at `Views/Partials/Forms/Themes/default/Fieldtypes/FieldType.<Name>.cshtml`. The folder is **`Fieldtypes`** (lower-case t), as in Forms' own resolver. The plan and design wrote `FieldTypes`.
- The RCL must set `<AddRazorSupportForMvc>true</AddRazorSupportForMvc>`. Without it no `.cshtml` is compiled into the package and the form fails with "partial view not found".
- A form rendered with a custom theme that has no field views falls back to the RCL's default view [run, `?theme=custom`]. A theme that does ship its own `Fieldtypes/FieldType.<Name>.cshtml` wins [static, `FormThemeResolver.GetFieldView`]. Forms also checks `Views/Partials/Forms/<formId>/Fieldtypes/` first.

**Entries, export, email** [run]: see the outcome above. The default "Send email" workflow HTML-encodes the raw value, so the JSON shows with `&quot;`.

**Other facts worth keeping**

- Both the 17 and the 18 floor behave the same, so no `#if UMBRACO_17` split is needed for the field type or its view.
- The Forms integration-test host needs `AddUmbracoForms()` (the `Umbraco.Forms` metapackage) once the package references Forms, because Forms' composers are discovered but its services are not added by the CMS test host.

## S2: backoffice preview element

- A manifest of `type: "formsFieldPreview"` with an `alias` equal to the field's `PreviewView`, and an `element` pointing at `/App_Plugins/AddressLookup/backoffice/preview.js`, placed in `wwwroot/umbraco-package.json`, is accepted [run: it appears in the private manifest API on 17 and 18; the script is served with 200].
- Forms' own `umbraco-package.json` declares the import map entry `@umbraco-forms/backoffice` → `/App_Plugins/UmbracoForms/umbraco-forms.js`, and that module exports `FormsFieldPreviewBaseElement` [static]. Its built-in previews are plain elements extending that base class and using `getSettingValue(...)` [static].
- **[not run]** The preview element rendering inside the form designer was not checked in a browser: the build session had no way to log in to the backoffice without typing a password. Check by hand: boot the demo, open a form containing the field in the Forms section, and look for the preview. If the import fails, the fallback is a plain `UmbLitElement` registered the same way.

## Provider error shapes (Task 5, Task 6)

Built from the providers' documentation, **not yet seen against the live APIs** (**[not run]**: needs a Loqate trial key and an Ideal Postcodes key for the final real test).

- **Loqate** json6 answers 200 with `{"Items":[{"Error":"2","Description":"...","Cause":"...","Resolution":"..."}]}` on a key or account failure. The provider reads only `Error` (a string or a number), keeps it only if it is a run of up to six digits or hyphens, and drops `Description`, `Cause` and `Resolution` because they can echo the key. A non-2xx status gives `HTTP-nnn`. Codes covered by tests: 2, 3, 4, 8, 10, 14, 17, -1, 1001.
- **Ideal Postcodes** answers with a numeric `code` in the JSON body (2000 is success) and an HTTP 4xx or 5xx status. The provider reads `code` only, and treats 4048 as "not found" (empty result, or null on resolve). Codes covered by tests: 4010, 4011, 4020, 4021, 4291, 5001, 4048.
- Check on the first live run that a real Loqate failure matches the shape above. If it does not, the provider fails closed: a Find answer whose items have no readable `Id` and `Text` is `BAD-RESPONSE`, and an unparseable body is `BAD-RESPONSE`.
- **Ideal Postcodes key transport [unverified]:** the key is sent as `Authorization: IDEALPOSTCODES api_key="..."`, not in the query string, so it stays out of the request URI that logs and APM telemetry record. If the first live call returns 4010 or 4011 with a valid key, the header form is wrong: switch back to `api_key` in the query and keep the log filter. Also check on the first live run that `organisation_name` duplicates `line_1` for commercial premises (the provider adds it only when `line_1` does not already start with it).

## Task 10 results (M3: view, script, Capture mode, preview)

Run on 2026-11-23 against the demo site on both lines (CMS 17.8.0 with Forms 17.6.2, CMS 18.3.0 with Forms 18.2.2), SQLite, Stub provider, Chromium. Tags as above.

- **S2 follow-up [run, 17]:** `wwwroot/backoffice/preview.js` imports in the real 17 backoffice (`@umbraco-forms/backoffice` resolves), its default export extends Forms' base element (`getSettingValue` is present), and it renders a disabled search box and the manual button. **[not run]** Rendering inside the Forms designer was not checked (needs a designer session by hand), and the 18 backoffice was not checked.
- **Forms posts back to the page it was rendered on [run]:** a form on a route with no Umbraco content cannot show its thank-you message or its validation errors, because the post is handled by the catch-all and answers with the "no content" welcome page. The demo therefore seeds three published pages (one per form) from a seeder, and the e2e specs open those.
- **Forms client validation needs a validation framework on the page [run]:** the demo template adds `aspnet-validation.min.js` from `/App_Plugins/UmbracoForms/assets/aspnet-client-validation/dist/`. Without it Forms' conditions script prints "Umbraco Forms requires a validation framework" and conditions do not run. A site that already renders Forms forms has this.
- **U1, multi-page form [run, 17 and 18]:** a picked address on page 1 survives "Next" and the final submit: the stored value is `source:"lookup"` with the provider id (read from the Forms record table). No change needed.
- **U1, field hidden by a condition [run, 17 and 18]:** a **mandatory** address field hidden by a condition blocked the submit with "Enter the first line of the address". Forms does not filter hidden fields before calling a field type's `ValidateField`; the base implementation does the condition check and our override did not call it. Fixed: when a mandatory field with a condition has nothing posted, `ValidateField` asks `base.ValidateField` with no values, and treats "no error" as hidden. Covered by the e2e spec only (the unit tests cannot build Forms' condition state), including the opposite case: a shown, empty, mandatory field still fails. It covers a condition on the field itself, and also a visitor who typed and then hid the field. **[not run]** A condition on the field's fieldset or page is not covered, and whether the base method honours it is unknown. **[unverified]** The base behaviour was observed on 17.6.2 and 18.2.2 only; recheck on a new Forms minor.
- **Forms' conditions run on `change` [run]:** a text rule field fires when it loses focus, not while typing.
- **No-JS [run]:** with JavaScript off the manual inputs are the whole field. The collapsed manual panel is `hidden` in the HTML, and a stylesheet inside `<noscript>` shows it again and hides the search block. A strict CSP that blocks that stylesheet leaves the manual panel hidden without JavaScript (README note).
- **Capture mode [run, 18, fake keys and a stubbed Loqate script]:** the page loads `https://api.addressy.com/js/address-4.05.js` and `.css`, calls `new pca.Address(fields, { key })` with the browser key, and the server key is absent from the HTML. **[unverified]** The v4 field mapping (`Line1`, `City`, `Province`, `PostalCode`, `CountryIso2`, the empty-name search field and the `countries.codesList` option) comes from memory of the v3 and v4 docs, not from a live widget: check on the first run with a real Loqate trial key.
- **Accessibility [run]:** `@axe-core/playwright` with `wcag2a`, `wcag2aa`, `wcag21aa`, `wcag22aa` reports no violations on the field closed, open (list showing) and with the manual inputs showing, on 17 and 18. **[not run]** The VoiceOver smoke test (Safari, macOS): needs a person. Check that the field label, the result count and the selection are announced.
- **Left unverified:** the diagnostics list (setup errors) shows when `ASPNETCORE_ENVIRONMENT` is Development or a backoffice user is detected on the front end. The backoffice-user path was not exercised.
- **Back in a multi-page form [run, 17 and 18]:** going Back posts the other page's inputs, so none of the field's inputs come back. Forms hands the view its stored value (`Model.Values`), and the field refills from that, with a fresh proof when the stored `source` was `lookup`. Tested end to end.
- **Search refused with a 400 [run]:** a 400 on a young page is shown as "not available", not as an expired page; only a page open for about 100 minutes or more says it has been open for a long time.

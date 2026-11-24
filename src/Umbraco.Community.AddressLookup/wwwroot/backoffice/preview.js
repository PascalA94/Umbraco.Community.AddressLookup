// The form designer's preview of the Address lookup field. Hand-written: no build, no npm dependency (design 2).
// Extends Forms' own base element so the field settings (Placeholder and so on) are readable with getSettingValue. If Forms
// ever stops exporting it, a plain UmbLitElement still renders the preview, only without live settings.
import { html, css, customElement } from '@umbraco-cms/backoffice/external/lit';

let Base;
try {
  ({ FormsFieldPreviewBaseElement: Base } = await import('@umbraco-forms/backoffice'));
} catch {
  // Falls through to the fallback below.
}
if (!Base) {
  ({ UmbLitElement: Base } = await import('@umbraco-cms/backoffice/lit-element'));
}

export class CommunityAddressLookupPreview extends Base {
  static styles = css`
    :host { display: block; }
    input, button { box-sizing: border-box; font: inherit; }
    input { width: 100%; }
    button { margin-top: var(--uui-size-space-2, 6px); }
  `;

  render() {
    const placeholder = this.getSettingValue?.('Placeholder') || 'Start typing a postcode or address';
    const manual = this.getSettingValue?.('AllowManualEntry');
    const showButton = manual !== 'false' && manual !== '0';
    return html`
      <input type="text" autocomplete="off" disabled readonly placeholder=${placeholder} />
      ${showButton ? html`<button type="button" disabled>Enter address manually</button>` : ''}
    `;
  }
}

customElement('community-address-lookup-preview')(CommunityAddressLookupPreview);

export default CommunityAddressLookupPreview;

// Address lookup field (design 4.3, 6). An ARIA 1.2 combobox over the site's own proxy, or Loqate Capture v4 in the browser.
// Vanilla ES module: no dependencies and no build. Every value from the server is written with textContent, never innerHTML.

const DEBOUNCE_MS = 250;
const MIN_CHARS = 3;
// Consecutive soft failures (a 503, or a network error) tolerated before search is given up for the page view.
const MAX_SOFT_FAILURES = 3;

// Search text the server accepts: letters, marks, digits, spaces and punctuation, minus "<" and ">". Mirrors
// LookupInput.IsAllowed in Core/Controllers/LookupInput.cs (Unicode categories L*, Mn, Mc, N*, P*, and U+0020 only as the space).
// Anything else makes the server answer 400, which the field treats as a dead ticket, so it is removed before the request.
const DISALLOWED_SEARCH_CHARS = /[^\p{L}\p{Mn}\p{Mc}\p{N}\p{P} ]|[<>]/gu;

function cleanSearchText(text) {
  return text.replace(DISALLOWED_SEARCH_CHARS, '').replace(/ {2,}/g, ' ').trim();
}

const TEXT = {
  found: (n) => (n === 1 ? '1 address found' : `${n} addresses found`),
  foundIn: (n, where) => `Showing ${n} ${n === 1 ? 'address' : 'addresses'} in ${where}`,
  none: 'No addresses found, enter it manually',
  more: '(more addresses)',
  picked: 'Address selected. Check the details below.',
  unavailable: 'Address search is not available right now. Enter the address manually.',
  expired: 'This page has been open for a long time, so address search has stopped. Enter the address manually.',
  throttled: 'There have been too many searches. Enter the address manually.',
  // Under RequireLookup the server refuses a manual entry, so offering the inputs would be a dead end.
  reload: 'Address search has stopped working. Reload the page and try again later.',
  retry: 'Address search is not working right now. Keep typing, or try again in a moment.',
  retryThrottled: 'There have been too many searches. Wait a moment, then try again.',
  badChars: 'Search with letters, numbers and punctuation only.',
};

function post(root, action, fields, signal) {
  const body = new URLSearchParams({ ...fields, ticket: root.dataset.ticket ?? '' });
  const token = root.dataset.token;
  if (token) body.set('__RequestVerificationToken', token);

  // Same origin only: a host that trusts a forwarded prefix header could otherwise be made to print a foreign endpoint.
  const url = new URL(`${root.dataset.endpoint}/${action}`, location.href);
  if (url.origin !== location.origin) return Promise.reject(new TypeError('cross-origin endpoint refused'));

  return fetch(url, {
    method: 'POST',
    credentials: 'same-origin',
    headers: { Accept: 'application/json', 'Content-Type': 'application/x-www-form-urlencoded' },
    body,
    signal,
    cache: 'no-store',
  });
}

class ProxyLookup {
  constructor(root, parts) {
    this.root = root;
    this.parts = parts;
    this.id = root.dataset.id;
    this.input = root.querySelector('[data-al-input]');
    this.list = root.querySelector('[data-al-list]');
    this.status = root.querySelector('[data-al-status]');
    this.message = root.querySelector('[data-al-message]');
    this.manual = root.querySelector('[data-al-manual]');
    this.manualButton = root.querySelector('[data-al-manual-button]');
    this.requireLookup = root.dataset.requireLookup === 'true';
    this.softFailures = 0;
    this.items = [];
    this.active = -1;
    this.container = null;
    this.containerText = '';
    this.controller = null;
    this.timer = 0;

    this.input.addEventListener('input', () => this.onInput());
    this.input.addEventListener('keydown', (e) => this.onKeydown(e));
    this.input.addEventListener('blur', () => this.close());
    // The input keeps focus while the pointer picks, so the list never takes focus from the field (WCAG 3.2.2).
    this.list.addEventListener('mousedown', (e) => e.preventDefault());
    this.list.addEventListener('click', (e) => {
      const option = e.target.closest('[role="option"]');
      if (option) this.pick(Number(option.dataset.index));
    });
    this.manualButton?.addEventListener('click', () => this.showManual(true));

    // The server renders the manual inputs open so that a blocked or broken script never hides them (design 3.2); they are
    // collapsed here, once the search box can work.
    if (this.manual.dataset.collapsed === 'true') this.manual.hidden = true;
    this.started = performance.now();
    this.picking = false;
  }

  onInput() {
    // A container belongs to the text it was found for, so typing starts a fresh search.
    this.container = null;
    this.containerText = '';
    clearTimeout(this.timer);
    this.controller?.abort();

    this.clearRetry();
    if (this.input.value.trim().length < MIN_CHARS) {
      this.close();
      this.say('');
      return;
    }
    this.timer = setTimeout(() => this.search(), DEBOUNCE_MS);
  }

  onKeydown(e) {
    const open = !this.list.hidden;
    switch (e.key) {
      case 'ArrowDown':
      case 'ArrowUp': {
        if (!this.items.length) return;
        e.preventDefault();
        if (!open) this.open();
        const step = e.key === 'ArrowDown' ? 1 : -1;
        const next = this.active === -1 ? (step === 1 ? 0 : this.items.length - 1) : (this.active + step + this.items.length) % this.items.length;
        this.setActive(next);
        break;
      }
      case 'Enter':
        // With the list open Enter is for the list, so it must not also submit the form.
        if (open) {
          e.preventDefault();
          if (this.active >= 0) this.pick(this.active);
        }
        break;
      case 'Escape':
        if (open) {
          e.preventDefault();
          this.close();
        } else if (this.input.value) {
          e.preventDefault();
          clearTimeout(this.timer);
          this.controller?.abort();
          this.input.value = '';
          this.items = [];
          this.container = null;
          this.say('');
        }
        break;
      // Home, End and Tab keep their native behaviour: Home and End move in the text, and Tab leaves without picking.
      default:
    }
  }

  async search() {
    const text = cleanSearchText(this.input.value);
    if (text.length < MIN_CHARS) {
      // Nothing searchable is left, so no request is made. The field stays usable: typing on starts a new search.
      this.close();
      if (this.input.value.trim().length >= MIN_CHARS) this.say(TEXT.badChars);
      return;
    }
    this.controller?.abort();
    const controller = (this.controller = new AbortController());
    const fields = { text };
    if (this.container) fields.container = this.container;

    let response;
    try {
      response = await post(this.root, 'Find', fields, controller.signal);
    } catch (error) {
      if (error?.name === 'AbortError') return;
      this.fail(undefined);
      return;
    }
    if (controller.signal.aborted) return;

    if (!response.ok) {
      this.fail(response.status);
      return;
    }

    let items;
    try {
      items = await response.json();
    } catch {
      this.fallBack(TEXT.unavailable);
      return;
    }
    if (controller.signal.aborted || !Array.isArray(items)) return;

    this.softFailures = 0;
    this.clearRetry();
    this.items = items;
    this.render();
    if (!items.length) {
      this.close();
      this.say(TEXT.none);
      return;
    }
    this.open();
    this.say(this.container ? TEXT.foundIn(items.length, this.containerText) : TEXT.found(items.length));
  }

  render() {
    this.active = -1;
    this.input.removeAttribute('aria-activedescendant');
    const options = this.items.map((item, index) => {
      const li = document.createElement('li');
      li.id = `${this.id}_option${index}`;
      li.setAttribute('role', 'option');
      li.setAttribute('aria-selected', 'false');
      li.dataset.index = String(index);

      const text = document.createElement('span');
      text.className = 'al__text';
      text.textContent = String(item.text ?? '');
      li.append(text);

      if (item.description) {
        const description = document.createElement('span');
        description.className = 'al__desc';
        description.textContent = ` ${item.description}`;
        li.append(description);
      }
      if (item.isContainer) {
        // Words, not colour, tell a visitor that this result opens a longer list (WCAG 1.4.1).
        const more = document.createElement('span');
        more.className = 'al__more';
        more.textContent = ` ${TEXT.more}`;
        li.append(more);
      }
      return li;
    });
    this.list.replaceChildren(...options);
  }

  open() {
    this.list.hidden = false;
    this.input.setAttribute('aria-expanded', 'true');
  }

  close() {
    this.list.hidden = true;
    this.input.setAttribute('aria-expanded', 'false');
    this.input.removeAttribute('aria-activedescendant');
    this.active = -1;
    for (const option of this.list.children) option.setAttribute('aria-selected', 'false');
  }

  setActive(index) {
    const options = [...this.list.children];
    options.forEach((option, i) => option.setAttribute('aria-selected', String(i === index)));
    this.active = index;
    this.input.setAttribute('aria-activedescendant', options[index].id);
    options[index].scrollIntoView?.({ block: 'nearest' });
  }

  async pick(index) {
    try {
      await this.pickCore(index);
    } finally {
      this.picking = false;
    }
  }

  async pickCore(index) {
    const item = this.items[index];
    if (!item) return;

    if (item.isContainer) {
      this.container = item.id;
      this.containerText = item.text;
      clearTimeout(this.timer);
      await this.search();
      return;
    }

    // One Retrieve per pick: it is the paid call, and a second Enter or click must not spend another.
    if (this.picking) return;
    this.picking = true;
    this.close();
    this.controller?.abort();
    const controller = (this.controller = new AbortController());
    let response;
    try {
      response = await post(this.root, 'Retrieve', { id: item.id }, controller.signal);
    } catch (error) {
      if (error?.name !== 'AbortError') this.fail(undefined);
      return;
    }
    if (controller.signal.aborted) return;
    if (!response.ok) {
      this.fail(response.status);
      return;
    }

    let result;
    try {
      result = await response.json();
    } catch {
      this.fallBack(TEXT.unavailable);
      return;
    }
    if (!result?.address || typeof result.proof !== 'string') {
      this.fallBack(TEXT.unavailable);
      return;
    }

    this.fill(result.address, result.proof);
    this.showManual(false);
    this.say(TEXT.picked);
    // The visitor checks the filled address, starting at line 1 (WCAG 3.3.7: nothing is asked twice).
    this.parts.line1?.focus();
  }

  // The values are exactly what Retrieve returned: the proof covers them, and an edit makes the entry a manual one.
  fill(address, proof) {
    const lines = Array.isArray(address.lines) ? address.lines : [];
    const set = (name, value) => {
      if (this.parts[name]) this.parts[name].value = value == null ? '' : String(value);
    };
    set('line1', lines[0]);
    set('line2', lines[1]);
    set('line3', lines[2]);
    set('town', address.town);
    set('county', address.county);
    set('postcode', address.postcode);
    set('country', address.countryIso2);
    set('providerId', address.providerId);
    set('proof', proof);
  }

  showManual(focus) {
    this.manual.hidden = false;
    this.manualButton?.setAttribute('aria-expanded', 'true');
    if (focus) this.parts.line1?.focus();
  }

  // A 503 (or a network error) is usually a blip, so search stays open and the visitor can try again. A 429 is the throttle
  // doing its job, so it still ends the search (design 6) unless the field is RequireLookup, where manual entry is no way out.
  fail(status) {
    const soft = status === undefined || status === 503 || (status === 429 && this.requireLookup);
    if (soft && ++this.softFailures < MAX_SOFT_FAILURES) {
      const text = status === 429 ? TEXT.retryThrottled : TEXT.retry;
      this.message.hidden = false;
      this.message.textContent = text;
      this.say(text);
      // A failed Retrieve leaves the chosen suggestion to try again.
      if (this.items.length && document.activeElement === this.input) this.open();
      return;
    }
    this.fallBack(this.reasonFor(status));
  }

  clearRetry() {
    if (this.root.classList.contains('al--failed')) return;
    this.message.hidden = true;
    this.message.textContent = '';
  }

  reasonFor(status) {
    if (this.requireLookup) return TEXT.reload;
    if (status === 429) return TEXT.throttled;
    // A 400 is an expired ticket or antiforgery token (a form page left open for about two hours); a younger page got it for
    // another reason, so the message must not blame the page.
    const ageMs = performance.now() - this.started;
    return status === 400 && ageMs > 100 * 60 * 1000 ? TEXT.expired : TEXT.unavailable;
  }

  // Ends the search for this page and leaves the manual inputs, so a visitor can always finish the form (not under RequireLookup).
  fallBack(reason) {
    clearTimeout(this.timer);
    this.controller?.abort();
    this.close();
    const hadFocus = document.activeElement === this.input;
    this.root.classList.add('al--failed');
    this.message.hidden = false;
    this.message.textContent = reason;
    // The search box is gone, so focus moves on (WCAG 2.4.3), and the polite status carries the reason because text set in
    // a region that was just revealed is not reliably announced (4.1.3).
    this.say(reason);
    if (this.requireLookup) {
      // The server refuses any entry that was not picked from the list, so manual inputs would be a dead end.
      this.manual.hidden = true;
      if (hadFocus) {
        this.message.tabIndex = -1;
        this.message.focus();
      }
      return;
    }
    this.showManual(false);
    if (hadFocus) this.parts.line1?.focus();
  }

  say(text) {
    this.status.textContent = text;
  }
}

// Capture mode (design 4.3): Loqate's v4 widget fills the same inputs. Its accessibility is Loqate's, and it can never prove a
// lookup, so the proof stays empty and the entry is stored as manual.
let captureLoad;

// One script and one stylesheet per page, however many address fields it holds: a second copy would redefine window.pca.
function loadCapture(version) {
  captureLoad ??= new Promise((resolve) => {
    // The script and the stylesheet live under different paths: /js/ for the script, /css/ for the stylesheet.
    const css = document.createElement('link');
    css.rel = 'stylesheet';
    css.href = `https://api.addressy.com/css/address-${version}.css`;
    document.head.append(css);

    const script = document.createElement('script');
    script.src = `https://api.addressy.com/js/address-${version}.js`;
    script.async = true;
    script.addEventListener('load', () => resolve(window.pca));
    script.addEventListener('error', () => resolve(undefined));
    document.head.append(script);
  });
  return captureLoad;
}

function initCapture(root, parts) {
  const key = root.dataset.captureKey;
  const version = root.dataset.captureVersion;
  if (!key || !/^\d+(\.\d+){0,3}$/.test(version ?? '')) return;

  loadCapture(version).then((pca) => {
    if (!pca?.Address) return;
    const id = root.dataset.id;
    const fields = [
      { element: id, field: '' },
      { element: `${id}_line1`, field: 'Line1', mode: pca.fieldMode?.DEFAULT },
      { element: `${id}_line2`, field: 'Line2', mode: pca.fieldMode?.POPULATE },
      { element: `${id}_line3`, field: 'Line3', mode: pca.fieldMode?.POPULATE },
      { element: `${id}_town`, field: 'City', mode: pca.fieldMode?.POPULATE },
      { element: `${id}_county`, field: 'Province', mode: pca.fieldMode?.POPULATE },
      { element: `${id}_postcode`, field: 'PostalCode', mode: pca.fieldMode?.POPULATE },
      { element: parts.country?.id || undefined, field: 'CountryIso2', mode: pca.fieldMode?.COUNTRY },
    ].filter((f) => f.element);
    try {
      new pca.Address(fields, { key, countries: { codesList: root.dataset.countries ?? 'GB' } });
    } catch {
      // The inputs are ordinary inputs: a widget that fails to start leaves manual entry working.
    }
  });
}

function init(root) {
  if (root.dataset.alReady) return;
  root.dataset.alReady = 'true';
  root.classList.add('al--js');

  const parts = {};
  for (const el of root.querySelectorAll('[data-al-part]')) parts[el.dataset.alPart] = el;

  if (root.dataset.mode === 'proxy') new ProxyLookup(root, parts);
  else if (root.dataset.mode === 'capture') initCapture(root, parts);
}

document.querySelectorAll('[data-address-lookup]').forEach(init);

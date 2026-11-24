import AxeBuilder from '@axe-core/playwright';
import { expect, test, type Locator, type Page } from '@playwright/test';

// Proxy mode against the Stub provider (Development only). No backoffice login, so none of the support/ helpers are used.
// Locators are scoped to the field and visible-only (README rule 11): never `.first()` of a page-wide locator.

const proxyOnly = process.env.E2E_MODE === 'capture';
test.skip(proxyOnly, 'These specs need a proxy-mode site; the capture spec runs against Capture mode.');

const THANKS = 'Thanks, we have your details.';

function field(page: Page): Locator {
  return page.locator('[data-address-lookup]:visible');
}

const combobox = (root: Locator) => root.getByRole('combobox');
const options = (root: Locator) => root.getByRole('option');
const part = (root: Locator, name: string) => root.locator(`[data-al-part="${name}"]`);
const status = (root: Locator) => root.locator('[data-al-status]');
const message = (root: Locator) => root.locator('[data-al-message]');

async function open(page: Page, slug = 'single') {
  const response = await page.goto(`/address-demo-${slug}`);
  expect(response?.status(), `the demo form "${slug}" should render`).toBe(200);
  return field(page);
}

async function search(root: Locator, text: string) {
  await combobox(root).fill(text);
  // The 250 ms debounce and the round trip: wait on the outcome, never on a timer.
  await expect(combobox(root)).toHaveAttribute('aria-expanded', 'true');
}

async function submit(page: Page) {
  await page.getByRole('button', { name: 'Send' }).filter({ visible: true }).click();
}

async function fillManually(root: Locator) {
  await part(root, 'line1').fill('1 Example Street');
  await part(root, 'town').fill('Teststone');
  await part(root, 'postcode').fill('ST1 1AA');
}

test('field settings reach the view', async ({ page }) => {
  const root = await open(page);
  await expect(combobox(root)).toHaveAttribute('placeholder', 'Postcode or address');
  await expect(combobox(root)).toHaveAttribute('maxlength', '100');
});

test('page html carries no provider key', async ({ page }) => {
  // The rendered Razor view is covered only by this check: the unit tests cover the model it prints, not the markup.
  const response = await page.goto('/address-demo-single');
  const html = await response!.text();
  // Booleans, not toContain: a failing assertion would print the whole page into the report artefacts.
  expect(/api_?key/i.test(html), 'no key name should be in the page').toBe(false);
  const serverKey = process.env.E2E_SERVER_KEY;
  if (serverKey) {
    expect(html.includes(serverKey), 'the server key must not be in the page').toBe(false);
  }
});

test('keyboard pick fills fields', async ({ page }) => {
  const root = await open(page);
  await search(root, 'ST1');
  await expect(options(root)).toHaveCount(3);

  await combobox(root).press('ArrowDown');
  await combobox(root).press('ArrowDown');
  await expect(combobox(root)).toHaveAttribute('aria-activedescendant', /_option1$/);
  await combobox(root).press('Enter');

  await expect(part(root, 'line1')).toHaveValue('2 Example Street');
  await expect(part(root, 'town')).toHaveValue('Teststone');
  await expect(part(root, 'postcode')).toHaveValue('ST1 1AB');
  await expect(part(root, 'providerId')).toHaveValue('stub-a4');
  await expect(part(root, 'proof')).not.toHaveValue('');
  await expect(part(root, 'line1')).toBeFocused();
});

test('container drills in and announces count', async ({ page }) => {
  const root = await open(page);
  await search(root, 'ST1');

  await combobox(root).press('ArrowDown');
  await combobox(root).press('Enter');

  await expect(status(root)).toContainText('addresses in');
  await expect(options(root)).toHaveCount(3);
  await expect(combobox(root)).toBeFocused();
});

test('escape closes then clears', async ({ page }) => {
  const root = await open(page);
  await search(root, 'ST1');

  await combobox(root).press('Escape');
  await expect(combobox(root)).toHaveAttribute('aria-expanded', 'false');
  await expect(combobox(root)).toHaveValue('ST1');

  await combobox(root).press('Escape');
  await expect(combobox(root)).toHaveValue('');
});

test('tab leaves without picking', async ({ page }) => {
  const root = await open(page);
  await search(root, 'ST1');
  await combobox(root).press('ArrowDown');
  await combobox(root).press('ArrowDown');

  await combobox(root).press('Tab');

  await expect(combobox(root)).toHaveAttribute('aria-expanded', 'false');
  await expect(part(root, 'line1')).toHaveValue('');
  await expect(part(root, 'proof')).toHaveValue('');
});

test('manual entry button works', async ({ page }) => {
  const root = await open(page);
  await expect(part(root, 'line1')).toBeHidden();

  await root.getByRole('button', { name: 'Enter address manually' }).click();

  await expect(part(root, 'line1')).toBeVisible();
  await expect(part(root, 'line1')).toBeFocused();
  await fillManually(root);
  await submit(page);
  await expect(page.getByText(THANKS)).toBeVisible();
});

/** Types a search and waits for the server's answer, so a later search cannot abort the earlier request and skip its failure count. */
async function findAnswered(page: Page, root: Locator, text: string) {
  await Promise.all([
    page.waitForResponse((r) => r.url().includes('/AddressLookup/Find') && r.request().method() === 'POST'),
    combobox(root).fill(text),
  ]);
}

test('unavailable falls back to manual', async ({ page }) => {
  const root = await open(page);

  // One 503 is a blip and keeps search open; the third in a row ends it. The stub treats these spellings alike, and each is a different search so the client sends all three.
  await findAnswered(page, root, 'ZZ99 9ZZ');
  await expect(message(root)).toContainText('not working right now');
  await expect(combobox(root)).toBeVisible();
  await findAnswered(page, root, 'ZZ9 99ZZ');
  await expect(status(root)).toContainText('not working right now');
  await findAnswered(page, root, 'ZZ999ZZ');

  await expect(message(root)).toContainText('not available');
  await expect(part(root, 'line1')).toBeVisible();
  await expect(part(root, 'line1')).toBeFocused();
  await expect(status(root)).toContainText('not available');
  await expect(combobox(root)).toBeHidden();
  await fillManually(root);
  await submit(page);
  await expect(page.getByText(THANKS)).toBeVisible();
});

test('expired ticket falls back to manual', async ({ page }) => {
  await page.route('**/umbraco/surface/AddressLookup/Find', (route) =>
    route.fulfill({ status: 400, contentType: 'application/json', body: '{"error":"invalid"}' }));
  // The page has to look old before a 400 is blamed on it; the clock jumps ahead of the two-hour ticket lifetime.
  await page.clock.install();
  const root = await open(page);
  await page.clock.fastForward('01:55:00');

  await combobox(root).fill('ST1');
  await page.clock.runFor(500);

  await expect(message(root)).toContainText('open for a long time');
  await expect(part(root, 'line1')).toBeVisible();
  await fillManually(root);
  await page.clock.resume();
  await submit(page);
  await expect(page.getByText(THANKS)).toBeVisible();
});

test('a refused search on a young page does not blame the page', async ({ page }) => {
  await page.route('**/umbraco/surface/AddressLookup/Find', (route) =>
    route.fulfill({ status: 400, contentType: 'application/json', body: '{"error":"invalid"}' }));
  const root = await open(page);

  await combobox(root).fill('ST1');

  await expect(message(root)).toContainText('not available');
  await expect(message(root)).not.toContainText('open for a long time');
});

test('a symbol in the search text does not end the search', async ({ page }) => {
  const sent: string[] = [];
  await page.route('**/umbraco/surface/AddressLookup/Find', async (route) => {
    sent.push(new URLSearchParams(route.request().postData() ?? '').get('text') ?? '');
    await route.continue();
  });
  const root = await open(page);

  await combobox(root).fill('Flat 1+2');
  await expect.poll(() => sent.length).toBeGreaterThan(0);
  // The symbol is removed on the client, so the server never answers 400 and the search stays usable.
  expect(sent[0]).toBe('Flat 12');
  await expect(combobox(root)).toBeVisible();
  await expect(root).not.toHaveClass(/al--failed/);

  await search(root, 'ST1');
  await expect(options(root)).toHaveCount(3);
});

test('search stays open after a single 503 and recovers', async ({ page }) => {
  let calls = 0;
  await page.route('**/umbraco/surface/AddressLookup/Find', async (route) => {
    calls += 1;
    if (calls === 1) {
      await route.fulfill({ status: 503, contentType: 'application/json', body: '{"error":"unavailable"}' });
      return;
    }
    await route.continue();
  });
  const root = await open(page);

  await combobox(root).fill('ST1');
  await expect(message(root)).toContainText('not working right now');
  await expect(combobox(root)).toBeVisible();

  await search(root, 'ST1 1');
  await expect(options(root)).not.toHaveCount(0);
  await expect(message(root)).toBeHidden();
});

test('lookup required hides the manual button', async ({ page }) => {
  const root = await open(page, 'required');
  await expect(combobox(root)).toBeVisible();
  await expect(root.getByRole('button', { name: 'Enter address manually' })).toHaveCount(0);
  // The combobox is never mandatory in itself: the required inputs are line 1, town and postcode.
  await expect(combobox(root)).not.toHaveAttribute('aria-required', 'true');
});

test('lookup required failure asks for a reload, not manual entry', async ({ page }) => {
  await page.route('**/umbraco/surface/AddressLookup/Find', (route) =>
    route.fulfill({ status: 503, contentType: 'application/json', body: '{"error":"unavailable"}' }));
  const root = await open(page, 'required');

  for (const text of ['ST1', 'ST1 1', 'ST1 1A']) await findAnswered(page, root, text);

  await expect(message(root)).toContainText('Reload the page');
  await expect(part(root, 'line1')).toBeHidden();
  await expect(combobox(root)).toBeHidden();
});

test('axe has no violations, closed and open', async ({ page }) => {
  const root = await open(page);
  const tags = ['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa'];
  const scan = async () => (await new AxeBuilder({ page }).include('[data-address-lookup]').withTags(tags).analyze()).violations;

  expect(await scan(), 'closed').toEqual([]);

  await search(root, 'ST1');
  expect(await scan(), 'open').toEqual([]);

  // The open list sits over the button (it is a popup), so it is dismissed first, as a visitor would.
  await combobox(root).press('Escape');
  await root.getByRole('button', { name: 'Enter address manually' }).click();
  expect(await scan(), 'manual inputs showing').toEqual([]);
});

test('a two-page form keeps a picked address from the first page', async ({ page }) => {
  const root = await open(page, 'two-pages');
  await search(root, 'ST1');
  await combobox(root).press('ArrowDown');
  await combobox(root).press('ArrowDown');
  await combobox(root).press('Enter');
  await expect(part(root, 'proof')).not.toHaveValue('');

  await page.getByRole('button', { name: 'Next' }).filter({ visible: true }).click();
  await expect(page.getByLabel('Your name')).toBeVisible();
  await page.getByLabel('Your name').fill('A. Tester');
  await submit(page);

  await expect(page.getByText(THANKS)).toBeVisible();
});

test('going Back keeps the picked address', async ({ page }) => {
  const root = await open(page, 'two-pages');
  await search(root, 'ST1');
  await combobox(root).press('ArrowDown');
  await combobox(root).press('ArrowDown');
  await combobox(root).press('Enter');
  await expect(part(root, 'proof')).not.toHaveValue('');

  await page.getByRole('button', { name: 'Next' }).filter({ visible: true }).click();
  await expect(page.getByLabel('Your name')).toBeVisible();
  await page.getByRole('button', { name: 'Back' }).filter({ visible: true }).click();

  const again = field(page);
  await expect(part(again, 'line1')).toHaveValue('2 Example Street');
  await expect(part(again, 'postcode')).toHaveValue('ST1 1AB');
  await expect(part(again, 'proof')).not.toHaveValue('');
});

test('a field hidden by a condition does not block the submit', async ({ page }) => {
  await page.goto('/address-demo-conditional');
  await expect(page.locator('[data-address-lookup]')).toBeHidden();

  await page.getByLabel('Delivery method').fill('email');
  await submit(page);
  await expect(page.getByText(THANKS)).toBeVisible();

  await page.goto('/address-demo-conditional');
  await page.getByLabel('Delivery method').fill('post');
  // Forms evaluates its conditions on change, which a text input fires when it loses focus.
  await page.getByLabel('Delivery method').blur();
  const root = field(page);
  await expect(root).toBeVisible();
  await search(root, 'ST1');

  // Shown and mandatory with nothing entered must still fail: the hidden-field skip must not make it optional.
  await page.goto('/address-demo-conditional');
  await page.getByLabel('Delivery method').fill('post');
  await page.getByLabel('Delivery method').blur();
  await expect(field(page)).toBeVisible();
  await page.getByLabel('Delivery note').fill('leave at the door');
  await submit(page);
  await expect(page.getByText('Enter the first line of the address')).toBeVisible();
  await expect(page.getByText(THANKS)).toBeHidden();
});

test.describe('no JavaScript', () => {
  test.use({ javaScriptEnabled: false });

  test('shows manual inputs and saves', async ({ page }) => {
    const root = await open(page);

    await expect(part(root, 'line1')).toBeVisible();
    await expect(root.getByRole('combobox')).toBeHidden();
    await expect(root.getByRole('button', { name: 'Enter address manually' })).toBeHidden();
    await fillManually(root);
    await submit(page);
    await expect(page.getByText(THANKS)).toBeVisible();
  });
});

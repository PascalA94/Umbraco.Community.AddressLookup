import { expect, test } from '@playwright/test';

// Capture mode. Needs a demo site started with AddressLookup__Provider=Loqate, AddressLookup__Mode=Capture and two different
// fake keys (never a real one; Loqate is not called, its script is answered here). The keys are passed in, never written down.

const serverKey = process.env.E2E_SERVER_KEY;
const captureKey = process.env.E2E_CAPTURE_KEY;
test.skip(process.env.E2E_MODE !== 'capture' || !serverKey || !captureKey, 'Set E2E_MODE=capture, E2E_SERVER_KEY and E2E_CAPTURE_KEY.');

test('capture mode loads v4 script without ApiKey', async ({ page }) => {
  const script = page.waitForRequest('https://api.addressy.com/js/address-4.05.js');
  // Loqate serves the stylesheet from /css/, not /js/ (a /js/ URL is a 404 and leaves the widget unstyled).
  const stylesheet = page.waitForRequest('https://api.addressy.com/css/address-4.05.css');
  await page.route('https://api.addressy.com/**', (route) =>
    route.fulfill({
      status: 200,
      contentType: route.request().url().endsWith('.css') ? 'text/css' : 'text/javascript',
      body: route.request().url().endsWith('.css')
        ? ''
        : 'window.pca = { fieldMode: {}, Address: function (fields, options) { window.__capture = { fields, options }; } };',
    }));

  await page.goto('/address-demo-single');
  await script;
  await stylesheet;

  const root = page.locator('[data-address-lookup]:visible');
  await expect(root).toHaveAttribute('data-mode', 'capture');
  await expect(root.locator('[data-al-part="line1"]')).toBeVisible();
  await expect.poll(() => page.evaluate(() => (window as unknown as { __capture?: { options: { key: string } } }).__capture?.options.key)).toBe(captureKey);

  const html = await page.content();
  // Booleans, not toContain: a failing assertion would print the key and the whole page into the report artefacts.
  expect(html.includes(serverKey!), 'the server key must not be in the page').toBe(false);
  expect(html.includes(captureKey!), 'the capture key should be in the page').toBe(true);
});

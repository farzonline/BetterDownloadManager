// Shared test harness: real Chromium with the unpacked BDM extension, plus helpers to read what it caught.
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright-core';
import assert from 'node:assert/strict';

export const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
export const EXTENSION = path.join(ROOT, 'extension');

export async function startBrowser(corpusUrl) {
  const profileDir = fs.mkdtempSync(path.join(os.tmpdir(), 'bdm-e2e-profile-'));
  const context = await chromium.launchPersistentContext(profileDir, {
    channel: 'chromium',
    headless: true,
    args: [`--disable-extensions-except=${EXTENSION}`, `--load-extension=${EXTENSION}`],
  });
  let [worker] = context.serviceWorkers().filter((w) => w.url().startsWith('chrome-extension://'));
  worker ??= await context.waitForEvent('serviceworker', { predicate: (w) => w.url().startsWith('chrome-extension://'), timeout: 20_000 });
  const extensionId = new URL(worker.url()).host;

  /** Everything the extension has found, across tabs. */
  async function registry() {
    const p = await context.newPage();
    try {
      await p.goto(`chrome-extension://${extensionId}/options/options.html`);
      const snap = await p.evaluate(() => chrome.runtime.sendMessage({ type: 'debug-registry' }));
      return Object.values(snap).flat();
    } finally { await p.close(); }
  }

  /** Opens a corpus page, waits for its player code, and returns the page (callers close it). */
  async function visit(pathname) {
    const page = await context.newPage();
    await page.goto(corpusUrl + pathname);
    await page.waitForFunction(() => window.__done === true, null, { timeout: 25_000 });
    return page;
  }

  /** Items appear asynchronously (response headers, body reads): poll for the one the test expects. */
  async function eventually(predicate, what, ms = 8000) {
    const end = Date.now() + ms;
    let items = [];
    while (Date.now() < end) {
      items = await registry();
      const hit = items.find(predicate);
      if (hit) return hit;
      await new Promise((r) => setTimeout(r, 250));
    }
    assert.fail(`${what} was not caught. Found instead: ${JSON.stringify(items.map((i) => [i.kind, i.via, i.url]))}`);
  }

  async function close() {
    await context.close();
    fs.rmSync(profileDir, { recursive: true, force: true });
  }

  return { context, extensionId, registry, visit, eventually, close };
}

import vm from 'node:vm';
import fs from 'node:fs';

/** Runs one of the extension's classic scripts inside a fresh context and returns that context. */
export function loadScript(rel, sandbox = {}) {
  const ctx = { TextDecoder, TextEncoder, console, setTimeout, clearTimeout, ...sandbox };
  ctx.window = ctx;
  ctx.globalThis = ctx;
  vm.createContext(ctx);
  vm.runInContext(fs.readFileSync(new URL(`../src/${rel}`, import.meta.url), 'utf8'), ctx);
  return ctx;
}

export const tick = () => new Promise((r) => setImmediate(r));
export const settle = async () => { for (let i = 0; i < 5; i++) await tick(); };

export class FakeEl {
  constructor(tagName, props = {}) {
    this.tagName = tagName.toUpperCase();
    this.children = [];
    this.parentElement = null;
    this.attrs = {};
    this.style = {};
    this.shadowRoot = null;
    this.textContent = '';
    Object.assign(this, props);
    for (const c of this.children) c.parentElement = this;
  }
  append(...kids) { for (const k of kids) { k.parentElement = this; this.children.push(k); } }
  appendChild(k) { this.append(k); return k; }
  remove() { if (this.parentElement) this.parentElement.children = this.parentElement.children.filter((c) => c !== this); this.parentElement = null; }
  setAttribute(n, v) { this.attrs[n] = String(v); }
  getAttribute(n) { return this.attrs[n] ?? null; }
  addEventListener(type, fn) { (this.listeners ??= {})[type] = [...(this.listeners?.[type] ?? []), fn]; }
  getBoundingClientRect() { return this.rect ?? { top: 100, left: 50, right: 450, bottom: 300, width: 400, height: 200 }; }
  querySelectorAll(sel) {
    const want = sel === '*' ? null : new Set(sel.split(',').map((s) => s.trim().toUpperCase()));
    const out = [];
    const walk = (e) => { for (const c of e.children) { if (!want || want.has(c.tagName)) out.push(c); walk(c); } };
    walk(this);
    return out;
  }
}

export class FakeDocument extends FakeEl {
  constructor() {
    super('#document');
    this.documentElement = new FakeEl('html');
    this.body = new FakeEl('body');
    this.documentElement.append(this.body);
    this.children = [this.documentElement];
    this.handlers = {};
  }
  createElement(tag) { return new FakeEl(tag); }
  addEventListener(type, fn) { (this.handlers[type] ??= []).push(fn); }
  async fire(type, event) { for (const h of this.handlers[type] ?? []) await h(event); }
}

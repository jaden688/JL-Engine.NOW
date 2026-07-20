// extension/page_hands.ts
// The operator's hands inside the real browser — plus its "tells" so you can SEE
// what it's doing. Every action runs against the active tab via
// chrome.scripting.executeScript. Before it clicks or fills, it flashes a visible
// marker on the target element, so browser control is observable, not invisible.

export interface PageInfo {
  url: string;
  title: string;
  text: string;
  links: Array<{ text: string; href: string }>;
}

/** Structured result for every hand action — feeds the activity timeline. */
export interface ActionResult {
  ok: boolean;
  action: string;      // "click", "fill", "navigate", "read", "tabs"
  detail: string;      // human-readable summary
  target?: string;     // selector/url the action touched
}

async function activeTab(): Promise<chrome.tabs.Tab> {
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  if (!tab?.id) throw new Error("No active tab.");
  return tab;
}

async function runInTab<T>(func: (...args: string[]) => T, ...args: string[]): Promise<T> {
  const tab = await activeTab();
  const [result] = await chrome.scripting.executeScript({ target: { tabId: tab.id! }, func, args });
  return result.result as T;
}

/**
 * Flash a labeled outline on an element so the user can watch the operator work.
 * Injected as a self-contained function; no persistent content script.
 */
export async function highlight(selectorOrText: string, label: string): Promise<boolean> {
  return runInTab((needle: string, tag: string) => {
    const find = (): HTMLElement | null => {
      try { const el = document.querySelector(needle) as HTMLElement | null; if (el) return el; } catch { /* not a selector */ }
      const all = document.querySelectorAll<HTMLElement>("a, button, [role=button], input, textarea, select");
      for (const c of all) {
        const t = (c.innerText ?? (c as HTMLInputElement).value ?? "").trim().toLowerCase();
        if (t === needle.trim().toLowerCase()) return c;
      }
      return null;
    };
    const el = find();
    if (!el) return false;
    el.scrollIntoView({ block: "center", behavior: "smooth" });
    const r = el.getBoundingClientRect();
    const box = document.createElement("div");
    Object.assign(box.style, {
      position: "fixed", left: `${r.left - 4}px`, top: `${r.top - 4}px`,
      width: `${r.width + 8}px`, height: `${r.height + 8}px`,
      border: "2px solid #5ee0c0", borderRadius: "6px",
      boxShadow: "0 0 0 3px rgba(94,224,192,.25), 0 0 18px rgba(94,224,192,.5)",
      zIndex: "2147483647", pointerEvents: "none",
      transition: "opacity .4s ease", opacity: "1",
    } as CSSStyleDeclaration);
    const chip = document.createElement("div");
    chip.textContent = tag;
    Object.assign(chip.style, {
      position: "absolute", left: "0", top: "-20px",
      background: "#5ee0c0", color: "#04120e", font: "600 11px ui-monospace, monospace",
      padding: "1px 6px", borderRadius: "4px", whiteSpace: "nowrap",
    } as CSSStyleDeclaration);
    box.appendChild(chip);
    document.body.appendChild(box);
    setTimeout(() => { box.style.opacity = "0"; setTimeout(() => box.remove(), 400); }, 900);
    return true;
  }, selectorOrText, label);
}

/** Read the visible page: url, title, main text (capped), and links. */
export async function readPage(): Promise<PageInfo & ActionResult> {
  const info = await runInTab(() => {
    const pick = (root: Element | null): string =>
      ((root ?? document.body) as HTMLElement)?.innerText?.replace(/\n{3,}/g, "\n\n").slice(0, 20000) ?? "";
    const main =
      document.querySelector("main") ?? document.querySelector("article") ?? document.querySelector('[role="main"]');
    const links = Array.from(document.querySelectorAll("a[href]"))
      .slice(0, 80)
      .map((a) => ({ text: (a.textContent ?? "").trim().slice(0, 90), href: (a as HTMLAnchorElement).href }))
      .filter((l) => l.text);
    return { url: location.href, title: document.title, text: pick(main), links };
  });
  return {
    ...info,
    ok: true,
    action: "read",
    detail: `${info.title} — ${info.text.length} chars, ${info.links.length} links`,
    target: info.url,
  };
}

/** Click the first element matching a CSS selector or exact visible text. */
export async function clickOn(selectorOrText: string): Promise<ActionResult> {
  await highlight(selectorOrText, "click");
  await new Promise((r) => setTimeout(r, 500)); // let the user see the marker first
  const detail = await runInTab((needle: string) => {
    let el: HTMLElement | null = null;
    try { el = document.querySelector(needle) as HTMLElement | null; } catch { /* not a selector */ }
    if (!el) {
      const all = document.querySelectorAll<HTMLElement>("a, button, [role=button], input[type=submit]");
      for (const cand of all) {
        if ((cand.innerText ?? cand.getAttribute("value") ?? "").trim().toLowerCase() === needle.trim().toLowerCase()) { el = cand; break; }
      }
    }
    if (!el) return `__MISS__`;
    el.scrollIntoView({ block: "center" });
    el.click();
    return `${el.tagName.toLowerCase()}${el.id ? "#" + el.id : ""} ("${(el.innerText ?? "").trim().slice(0, 60)}")`;
  }, selectorOrText);
  const ok = detail !== "__MISS__";
  return { ok, action: "click", target: selectorOrText, detail: ok ? `clicked ${detail}` : `no element for "${selectorOrText}"` };
}

/** Fill an input/textarea/select matching the selector. */
export async function fillField(selector: string, value: string): Promise<ActionResult> {
  await highlight(selector, "fill");
  await new Promise((r) => setTimeout(r, 500));
  const detail = await runInTab((sel: string, val: string) => {
    const el = document.querySelector(sel) as HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement | null;
    if (!el) return "__MISS__";
    (el as HTMLElement).focus();
    el.value = val;
    el.dispatchEvent(new Event("input", { bubbles: true }));
    el.dispatchEvent(new Event("change", { bubbles: true }));
    return `${sel}`;
  }, selector, value);
  const ok = detail !== "__MISS__";
  return { ok, action: "fill", target: selector, detail: ok ? `filled ${selector} = "${value.slice(0, 40)}"` : `no field for "${selector}"` };
}

/** Navigate the active tab. */
export async function navigate(url: string): Promise<ActionResult> {
  const tab = await activeTab();
  const full = /^https?:\/\//.test(url) ? url : `https://${url}`;
  await chrome.tabs.update(tab.id!, { url: full });
  return { ok: true, action: "navigate", target: full, detail: `navigating → ${full}` };
}

/** List open tabs in the current window. */
export async function listTabs(): Promise<ActionResult & { tabs: string[] }> {
  const tabs = await chrome.tabs.query({ currentWindow: true });
  const lines = tabs.map((t, i) => `${i}${t.active ? "*" : ""}: ${t.title?.slice(0, 60)} — ${t.url?.slice(0, 80)}`);
  return { ok: true, action: "tabs", detail: `${tabs.length} tabs`, tabs: lines };
}

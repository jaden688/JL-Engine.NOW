// web_engine/src/memory.ts
// IndexedDB-backed memory — the browser's answer to the Python engine's SQLite
// hybrid memory. Per-operator recent-interaction log with de-dup on store.

import type { ChatMessage } from "./types";

const DB_NAME = "jl_web_engine";
const STORE = "interactions";
const RECENT_LIMIT = 12;

interface Interaction {
  key?: number;
  operator: string;
  user: string;
  output: string;
  ts: number;
}

function openDB(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const req = indexedDB.open(DB_NAME, 1);
    req.onupgradeneeded = () => {
      const db = req.result;
      if (!db.objectStoreNames.contains(STORE)) {
        const os = db.createObjectStore(STORE, { keyPath: "key", autoIncrement: true });
        os.createIndex("operator", "operator", { unique: false });
      }
    };
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(req.error);
  });
}

function normalize(text: string): string {
  return (text || "").toLowerCase().split(/\s+/).filter(Boolean).join(" ");
}

export class WebMemory {
  private available: boolean;
  private fallback: Interaction[] = []; // in-memory if IndexedDB is blocked

  constructor() {
    this.available = typeof indexedDB !== "undefined";
  }

  async getContext(operator: string): Promise<{ recentInteractions: Interaction[] }> {
    if (!this.available) {
      return { recentInteractions: this.fallback.filter((i) => i.operator === operator).slice(-RECENT_LIMIT) };
    }
    const db = await openDB();
    return new Promise((resolve) => {
      const tx = db.transaction(STORE, "readonly");
      const idx = tx.objectStore(STORE).index("operator");
      const out: Interaction[] = [];
      idx.openCursor(IDBKeyRange.only(operator)).onsuccess = (e) => {
        const cursor = (e.target as IDBRequest<IDBCursorWithValue>).result;
        if (cursor) { out.push(cursor.value as Interaction); cursor.continue(); }
        else resolve({ recentInteractions: out.slice(-RECENT_LIMIT) });
      };
    });
  }

  /** Store a turn unless it duplicates the immediately preceding one. */
  async store(operator: string, user: string, output: string): Promise<void> {
    const ctx = await this.getContext(operator);
    const last = ctx.recentInteractions.at(-1);
    if (last && normalize(last.output) === normalize(output) && normalize(last.user) === normalize(user)) {
      return;
    }
    const record: Interaction = { operator, user, output, ts: Date.now() };
    if (!this.available) { this.fallback.push(record); return; }
    const db = await openDB();
    await new Promise<void>((resolve, reject) => {
      const tx = db.transaction(STORE, "readwrite");
      tx.objectStore(STORE).add(record);
      tx.oncomplete = () => resolve();
      tx.onerror = () => reject(tx.error);
    });
  }

  /** Recent turns rendered as chat messages for prompt context. */
  async asMessages(operator: string): Promise<ChatMessage[]> {
    const { recentInteractions } = await this.getContext(operator);
    const msgs: ChatMessage[] = [];
    for (const it of recentInteractions) {
      if (it.user) msgs.push({ role: "user", content: it.user });
      if (it.output) msgs.push({ role: "assistant", content: it.output });
    }
    return msgs;
  }
}

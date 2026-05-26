// File-based payload store with an in-memory index for fast UI listing.
// Each received payload is written to <dataDir>/payloads/<id>.json and added
// to an in-memory list so the debug UI doesn't need to re-read the disk.

import { promises as fs } from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';

export class PayloadStore {
  constructor({ dataDir, maxIndexSize }) {
    this.dataDir = path.resolve(dataDir);
    this.payloadDir = path.join(this.dataDir, 'payloads');
    this.confirmationsFile = path.join(this.dataDir, 'confirmations.json');
    this.maxIndexSize = maxIndexSize;
    this.index = []; // [{ id, receivedAt, summary }] — newest first
    this.confirmations = []; // [{ document_project_token, native_id, aireno_backpack_id, ... }]
  }

  async init() {
    await fs.mkdir(this.payloadDir, { recursive: true });
    await this.#hydrateIndexFromDisk();
    await this.#hydrateConfirmations();
  }

  // ── Writeback confirmations ──────────────────────────────────────────────────

  /// Adds (or updates) a confirmation. Keyed by (document_project_token, native_id).
  async addConfirmation(c) {
    if (!c.document_project_token || !c.native_id) {
      throw new Error('document_project_token and native_id required');
    }
    this.confirmations = this.confirmations.filter(
      (x) => !(x.document_project_token === c.document_project_token && x.native_id === c.native_id)
    );
    this.confirmations.push({
      document_project_token: c.document_project_token,
      native_id: c.native_id,
      aireno_backpack_id: c.aireno_backpack_id ?? null,
      confirmed_label: c.confirmed_label ?? null,
      confirmed_room_id: c.confirmed_room_id ?? null,
      confirmed_at: new Date().toISOString(),
    });
    await this.#persistConfirmations();
  }

  getConfirmations(documentProjectToken) {
    if (!documentProjectToken) return this.confirmations;
    return this.confirmations.filter((c) => c.document_project_token === documentProjectToken);
  }

  async clearConfirmations() {
    this.confirmations = [];
    await this.#persistConfirmations();
  }

  async #hydrateConfirmations() {
    try {
      const raw = await fs.readFile(this.confirmationsFile, 'utf8');
      this.confirmations = JSON.parse(raw);
    } catch (err) {
      if (err.code !== 'ENOENT') console.warn('confirmations hydrate failed:', err.message);
      this.confirmations = [];
    }
  }

  async #persistConfirmations() {
    await fs.writeFile(this.confirmationsFile, JSON.stringify(this.confirmations, null, 2), 'utf8');
  }

  async save({ payload, tokenPreview, sourceIp }) {
    const id = this.#newId();
    const receivedAt = new Date().toISOString();

    const envelope = {
      id,
      received_at: receivedAt,
      bearer_token_preview: tokenPreview,
      source_ip: sourceIp,
      payload,
    };

    const file = path.join(this.payloadDir, `${id}.json`);
    await fs.writeFile(file, JSON.stringify(envelope, null, 2), 'utf8');

    const summary = this.#summarize(payload);
    this.index.unshift({ id, receivedAt, summary });
    if (this.index.length > this.maxIndexSize) {
      this.index.length = this.maxIndexSize;
    }

    return { id, receivedAt };
  }

  listSummaries() {
    return this.index;
  }

  async get(id) {
    if (!this.#isSafeId(id)) return null;
    try {
      const file = path.join(this.payloadDir, `${id}.json`);
      const raw = await fs.readFile(file, 'utf8');
      return JSON.parse(raw);
    } catch (err) {
      if (err.code === 'ENOENT') return null;
      throw err;
    }
  }

  async clearAll() {
    const entries = await fs.readdir(this.payloadDir).catch(() => []);
    await Promise.all(
      entries
        .filter((e) => e.endsWith('.json'))
        .map((e) => fs.unlink(path.join(this.payloadDir, e)).catch(() => {}))
    );
    this.index = [];
  }

  // ── internals ─────────────────────────────────────────────────────────────

  #newId() {
    // Sortable timestamp prefix + random suffix → newest sorts last alphabetically,
    // which makes filesystem inspection easier.
    const ts = new Date().toISOString().replace(/[:.]/g, '-');
    const suffix = crypto.randomBytes(4).toString('hex');
    return `${ts}_${suffix}`;
  }

  #isSafeId(id) {
    return typeof id === 'string' && /^[A-Za-z0-9_\-]+$/.test(id);
  }

  #summarize(payload) {
    if (!payload || typeof payload !== 'object') {
      return { source_software: 'unknown', total_objects: 0 };
    }
    // Canonical Schema v0.2: source_software / plugin_version / extraction_trigger
    // are inside each object's group_7_source (duplicated per spec) — NOT at top-level.
    // Pull from the first object's G7; fall back to old top-level location for legacy payloads
    // generated before the schema move.
    const firstG7 = payload.objects?.[0]?.group_7_source ?? {};
    return {
      schema_version: payload.aireno_schema_version,
      source_software:         firstG7.source_software         ?? payload.source_software,
      source_software_version: firstG7.source_software_version ?? payload.source_software_version,
      plugin_version:          firstG7.plugin_version          ?? payload.plugin_version,
      document_project_token:  payload.document_project_token  ?? firstG7.document_project_token,
      extraction_trigger:      firstG7.extraction_trigger      ?? payload.extraction_trigger,
      extracted_at:            firstG7.extracted_at            ?? payload.extracted_at,
      total_objects: payload.extraction_summary?.total_objects ?? (payload.objects?.length ?? 0),
      total_rooms: payload.extraction_summary?.total_rooms ?? (payload.rooms?.length ?? 0),
      total_annotations: payload.annotations?.length ?? 0,
      total_dimensions: payload.dimensions?.length ?? 0,
      total_hatches: payload.hatches?.length ?? 0,
    };
  }

  async #hydrateIndexFromDisk() {
    const entries = await fs.readdir(this.payloadDir).catch(() => []);
    const files = entries.filter((e) => e.endsWith('.json')).sort().reverse();
    const recent = files.slice(0, this.maxIndexSize);

    for (const file of recent) {
      try {
        const raw = await fs.readFile(path.join(this.payloadDir, file), 'utf8');
        const envelope = JSON.parse(raw);
        this.index.push({
          id: envelope.id,
          receivedAt: envelope.received_at,
          summary: this.#summarize(envelope.payload),
        });
      } catch {
        // skip malformed file
      }
    }
  }
}

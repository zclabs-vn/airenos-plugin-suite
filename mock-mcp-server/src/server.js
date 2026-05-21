// AirenoOS Mock MCP Server
// One HTTP endpoint shared by all 3 plugins (AutoCAD, BricsCAD, Revit).
// Receives Canonical Plugin Schema v0.2 payloads, persists them, and exposes
// a small debug UI for inspection.

import 'dotenv/config';
import express from 'express';
import cors from 'cors';
import morgan from 'morgan';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import crypto from 'node:crypto';
import { PayloadStore } from './storage.js';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const projectRoot = path.resolve(__dirname, '..');

const PORT = Number(process.env.PORT) || 5099;
const DATA_DIR = path.resolve(projectRoot, process.env.DATA_DIR || './data');
const MAX_INDEX_SIZE = Number(process.env.MAX_INDEX_SIZE) || 500;
const ALLOWED_TOKENS = (process.env.ALLOWED_TOKENS || '')
  .split(',')
  .map((s) => s.trim())
  .filter(Boolean);

const store = new PayloadStore({ dataDir: DATA_DIR, maxIndexSize: MAX_INDEX_SIZE });
await store.init();

const app = express();

app.use(cors());
app.use(express.json({ limit: '50mb' }));
app.use(morgan('dev'));
app.use(express.static(path.join(projectRoot, 'public')));

// ── Middleware: bearer auth ───────────────────────────────────────────────────

function bearerAuth(req, res, next) {
  const header = req.get('Authorization') || '';
  const match = header.match(/^Bearer\s+(.+)$/i);
  if (!match) {
    return res.status(401).json({ error: 'missing_bearer_token' });
  }
  const token = match[1].trim();
  if (!token) {
    return res.status(401).json({ error: 'empty_bearer_token' });
  }
  if (ALLOWED_TOKENS.length > 0 && !ALLOWED_TOKENS.includes(token)) {
    return res.status(403).json({ error: 'token_not_allowed' });
  }
  req.bearerToken = token;
  next();
}

// ── Health check (no auth) ────────────────────────────────────────────────────

app.get('/health', (_req, res) => {
  res.json({ status: 'ok', uptime_seconds: Math.round(process.uptime()) });
});

// ── Main plugin endpoint: POST /v1/extract ────────────────────────────────────

app.post('/v1/extract', bearerAuth, async (req, res) => {
  const payload = req.body;
  if (!payload || typeof payload !== 'object') {
    return res.status(400).json({ error: 'invalid_json_payload' });
  }

  const tokenPreview = req.bearerToken.slice(0, 6) + '…';
  const sourceIp = req.ip;

  try {
    const { id } = await store.save({ payload, tokenPreview, sourceIp });
    res.status(200).json({
      status: 'accepted',
      extraction_id: id,
    });
  } catch (err) {
    console.error('save failed', err);
    res.status(500).json({ error: 'persistence_failed' });
  }
});

// ── Debug API (no auth — assume server is on private network or behind nginx basic-auth) ──

app.get('/api/payloads', (_req, res) => {
  res.json(store.listSummaries());
});

app.get('/api/payloads/:id', async (req, res) => {
  const envelope = await store.get(req.params.id);
  if (!envelope) return res.status(404).json({ error: 'not_found' });
  res.json(envelope);
});

app.delete('/api/payloads', async (_req, res) => {
  await store.clearAll();
  res.json({ status: 'cleared' });
});

// ── Boot ──────────────────────────────────────────────────────────────────────

app.listen(PORT, () => {
  const url = `http://localhost:${PORT}`;
  console.log(`AirenoOS Mock MCP server listening on ${url}`);
  console.log(`  POST ${url}/v1/extract       — plugin endpoint (bearer auth required)`);
  console.log(`  GET  ${url}/                  — debug UI`);
  console.log(`  GET  ${url}/api/payloads      — JSON list`);
  console.log(`  GET  ${url}/health            — health check`);
  if (ALLOWED_TOKENS.length > 0) {
    console.log(`  Token allowlist: ${ALLOWED_TOKENS.length} entries`);
  } else {
    console.log(`  Token allowlist: (open — any non-empty bearer accepted)`);
  }
});

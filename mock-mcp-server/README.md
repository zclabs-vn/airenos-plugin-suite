# AirenoOS Mock MCP Server

A stand-in for the real AirenoOS MCP endpoint while it is being built. Receives extraction payloads from all three plugins (AutoCAD, BricsCAD, Revit) and exposes a small web UI for inspecting them. Used for local development and live demos to the client.

The HTTP contract follows the **Canonical Plugin Schema v0.2** spec — one shared endpoint and one shared payload shape across all three plugins; only the `source_software` field changes.

## What it does

- `POST /v1/extract` — accepts a JSON payload with a `Bearer` token header. Returns `200 { "status": "accepted", "extraction_id": "..." }`. Persists the payload to disk.
- `GET /` — browser UI listing every received payload, with a syntax-highlighted JSON viewer.
- `GET /api/payloads` — JSON summary list of received payloads.
- `GET /api/payloads/:id` — full envelope for a single payload.
- `DELETE /api/payloads` — clear all (debug only).
- `GET /health` — liveness probe.

## Run locally

```bash
cd mock-mcp-server
npm install
cp .env.example .env       # optional — defaults are fine for dev
npm run dev                # auto-restarts on file change
```

The server listens on `http://localhost:5099`. Open it in a browser to see the inspector UI.

### Smoke test

```bash
curl -X POST http://localhost:5099/v1/extract \
  -H "Authorization: Bearer test-token" \
  -H "Content-Type: application/json" \
  -d '{
    "aireno_schema_version": "0.2",
    "payload_type": "extraction",
    "source_software": "bricscad",
    "source_software_version": "26.2",
    "plugin_version": "1.0.0",
    "document_project_token": "demo-project",
    "extraction_trigger": "manual",
    "objects": [],
    "extraction_summary": { "total_objects": 0 }
  }'
```

Expect: `{"status":"accepted","extraction_id":"<id>"}`. Refresh the UI to see the entry.

### Point the BricsCAD plugin at this server

In BricsCAD, run `AIRENO_CONNECT` and enter:

- **Endpoint:** `http://localhost:5099/v1/extract`
- **Token:** any non-empty string (e.g. `dev-token`)

## Configuration (`.env`)

| Variable | Default | Meaning |
|---|---|---|
| `PORT` | `5099` | Listening port. |
| `DATA_DIR` | `./data` | Where received payloads are stored on disk. |
| `MAX_INDEX_SIZE` | `500` | How many payloads to keep in the in-memory list. Older payloads stay on disk but disappear from the UI. |
| `ALLOWED_TOKENS` | *(empty)* | Comma-separated bearer-token allowlist. **Empty = accept any non-empty token** (dev mode). Set this in production. |

## Deploy to a VPS

Uses the same pattern as other ZCLabs apps on the shared `103.253.145.163` box (staircraft, draftera, tubep3d, …): build locally → push to Docker Hub → SSH to VPS → pull + restart. The container binds to `127.0.0.1:5099`; system-level nginx fronts the public domain with a Let's Encrypt cert.

Live demo: **https://mock-mcp.demo-hub.dev/**

### One-time: prep the dev machine

```bash
cd mock-mcp-server
cp .deploy.env.example .deploy.env   # VPS_HOST etc — defaults match 103.253.145.163
docker login                          # if not already logged in to Docker Hub
chmod +x deploy.sh
```

### One-time: prep the VPS (already done for 103.253.145.163)

These steps were run once when first deploying to this host. Re-do them for any new VPS.

1. Make sure DNS for `mock-mcp.demo-hub.dev` points to the VPS (proxied through Cloudflare is fine — Cloudflare passes HTTP-01 challenges through).
2. Install the nginx vhost:
   ```bash
   scp deploy/nginx.conf.example root@<vps>:/etc/nginx/sites-available/mock-mcp.demo-hub.dev
   ssh root@<vps> "ln -sf /etc/nginx/sites-available/mock-mcp.demo-hub.dev /etc/nginx/sites-enabled/ \
                   && nginx -t && systemctl reload nginx"
   ```
3. Issue the Let's Encrypt cert (also rewrites the vhost to add HTTPS + redirect):
   ```bash
   ssh root@<vps> "certbot --nginx -d mock-mcp.demo-hub.dev --non-interactive \
                    --agree-tos --email admin@zclabs.dev --redirect"
   ```
4. Cert renewal is automatic via certbot's systemd timer — no further action needed.

### Deploy

```bash
./deploy.sh                  # build + push + deploy
./deploy.sh --no-cache       # full rebuild
./deploy.sh --skip-build     # use existing local image (faster iteration)
./deploy.sh --skip-deploy    # build + push only, don't touch VPS
./deploy.sh --help
```

The script:
1. Builds image `mrbo0911/aireno-mock-mcp:{latest,<git-sha>}`
2. Pushes to Docker Hub
3. SSHes to the VPS, copies `docker-compose.prod.yml`, pulls the new image, restarts the container
4. Health-checks `http://127.0.0.1:5099/health` from inside the VPS

After a successful deploy, the inspector UI is live at https://mock-mcp.demo-hub.dev/ and plugins POST to https://mock-mcp.demo-hub.dev/v1/extract.

### Lock down the bearer token (optional)

On the VPS, edit `/opt/aireno-mock-mcp/.env`:

```
ALLOWED_TOKENS=demo-brian-1,demo-cuong-1
```

Then `docker compose -f /opt/aireno-mock-mcp/docker-compose.prod.yml up -d` to apply. Plugins must use one of the listed tokens or they get `403 token_not_allowed`.

## File layout

```
mock-mcp-server/
├── src/
│   ├── server.js                # Express app, routes, middleware
│   └── storage.js               # File-backed payload store
├── public/
│   └── index.html               # Debug UI (vanilla — no build step)
├── data/                        # Received payloads (gitignored)
├── Dockerfile
├── docker-compose.yml           # Local dev — builds from source
├── docker-compose.prod.yml      # VPS — pulls pushed image, binds 127.0.0.1:5099
├── deploy.sh                    # Build + push + SSH-deploy from your machine
├── deploy/
│   └── nginx.conf.example       # One-time nginx vhost for the VPS
├── .deploy.env.example          # SSH/Docker-Hub config (copy to .deploy.env)
├── .env.example                 # Runtime config (copy to .env on the VPS)
└── package.json
```

## Notes for the demo

- Run `AIRENO_EXTRACT` in BricsCAD; refresh the inspector UI. The payload appears with `source_software: "bricscad"`. The same UI handles AutoCAD and Revit payloads with their respective tags.
- Brian's team can browse received payloads via the UI to verify the schema is being honored, without needing the real MCP backend up.
- All payloads are kept on the VPS in the `aireno_mock_mcp_payloads` Docker volume. Safe to `docker run --rm -v aireno_mock_mcp_payloads:/d alpine tar czf - /d > backup.tgz`.

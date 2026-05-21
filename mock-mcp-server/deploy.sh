#!/usr/bin/env bash
#
# Local-machine deploy script for the AirenoOS Mock MCP server.
# Pattern adapted from zclabs-vn/staircraft + zclabs-vn/tam-giam.
#
# Pipeline:
#   1. Build Docker image locally
#   2. Push to Docker Hub with two tags (latest + git-sha)
#   3. SSH to VPS, copy compose file, pull new image, restart container
#
# HTTPS routing on the VPS uses system-level nginx + Let's Encrypt
# (same pattern as staircraft, draftera, etc.). The vhost + cert are set up
# one time per host outside this script — see README "One-time VPS setup".
#
# Usage:
#   ./deploy.sh                # build + push + deploy to VPS
#   ./deploy.sh --skip-build   # use existing local image (skip step 1+2)
#   ./deploy.sh --skip-deploy  # build + push only, don't touch VPS
#   ./deploy.sh --no-cache     # full rebuild
#   ./deploy.sh --help
#
# Required (set in .deploy.env or as env vars):
#   DOCKER_USER  (default: mrbo0911)
#   VPS_HOST     (no default — e.g. 103.253.145.163)
#   VPS_USER     (default: root)

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
[ -f "$SCRIPT_DIR/.deploy.env" ] && source "$SCRIPT_DIR/.deploy.env"

DOCKER_USER="${DOCKER_USER:-mrbo0911}"
VPS_HOST="${VPS_HOST:-}"
VPS_USER="${VPS_USER:-root}"

SKIP_BUILD=false
SKIP_DEPLOY=false
NO_CACHE_FLAG=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --skip-build)  SKIP_BUILD=true ;;
    --skip-deploy) SKIP_DEPLOY=true ;;
    --no-cache)    NO_CACHE_FLAG="--no-cache" ;;
    --help|-h)
      sed -n '2,25p' "$0" | sed 's/^# //; s/^#//'
      exit 0 ;;
    *) echo "Unknown option: $1" >&2; exit 1 ;;
  esac
  shift
done

log()  { printf "\033[1;36m[deploy]\033[0m %s\n" "$*"; }
ok()   { printf "\033[1;32m[ok]\033[0m %s\n" "$*"; }
warn() { printf "\033[1;33m[warn]\033[0m %s\n" "$*"; }
fail() { printf "\033[1;31m[fail]\033[0m %s\n" "$*"; exit 1; }

require_cmd() { command -v "$1" >/dev/null 2>&1 || fail "Missing command: $1"; }

# ─── Pre-flight ──────────────────────────────────────────────────────────────
log "Pre-flight checks..."
require_cmd docker
require_cmd git
require_cmd ssh

docker info >/dev/null 2>&1 || fail "Docker daemon not reachable. Start Docker Desktop and retry."

if [[ "$SKIP_DEPLOY" != "true" ]]; then
  [ -z "$VPS_HOST" ] && fail "VPS_HOST not set (export VPS_HOST=... or add to .deploy.env)"
fi

GIT_SHA="$(git rev-parse --short HEAD 2>/dev/null || echo unknown)"
GIT_BRANCH="$(git rev-parse --abbrev-ref HEAD 2>/dev/null || echo detached)"
log "Git: $GIT_BRANCH @ $GIT_SHA"

# ─── Config ──────────────────────────────────────────────────────────────────
IMAGE_REPO="${DOCKER_USER}/aireno-mock-mcp"
IMAGE_LATEST_TAG="latest"
IMAGE_SHA_TAG="${GIT_SHA}"

COMPOSE_PROJECT_NAME="aireno-mock-mcp"
CONTAINER_PREFIX="aireno-mock-mcp"
HOST_PORT="5099"
PAYLOADS_VOLUME="aireno_mock_mcp_payloads"

DEPLOY_PATH="${DEPLOY_PATH:-/opt/aireno-mock-mcp}"
PUBLIC_URL="https://mock-mcp.demo-hub.dev"

log "Target: $PUBLIC_URL (path=$DEPLOY_PATH, tag=$IMAGE_SHA_TAG)"

# ─── Build ───────────────────────────────────────────────────────────────────
if [[ "$SKIP_BUILD" != "true" ]]; then
  # Git Bash on Windows MSYS path translation can corrupt build context paths.
  ctx_path="$SCRIPT_DIR"
  if command -v cygpath >/dev/null 2>&1; then
    ctx_path="$(cygpath -w "$SCRIPT_DIR")"
  fi

  log "Building image: ${IMAGE_REPO}:${IMAGE_LATEST_TAG} (+ :${IMAGE_SHA_TAG})"
  MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*' docker build $NO_CACHE_FLAG \
    -t "${IMAGE_REPO}:${IMAGE_LATEST_TAG}" \
    -t "${IMAGE_REPO}:${IMAGE_SHA_TAG}" \
    -f Dockerfile \
    "$ctx_path"
  ok "Built"

  log "Pushing to Docker Hub as $DOCKER_USER (run 'docker login' beforehand if needed)..."
  docker push "${IMAGE_REPO}:${IMAGE_LATEST_TAG}"
  docker push "${IMAGE_REPO}:${IMAGE_SHA_TAG}"
  ok "Pushed ${IMAGE_REPO}:{${IMAGE_LATEST_TAG},${IMAGE_SHA_TAG}}"
else
  warn "Skipping build (--skip-build)"
fi

# ─── Deploy on VPS ───────────────────────────────────────────────────────────
if [[ "$SKIP_DEPLOY" == "true" ]]; then
  warn "Skipping deploy (--skip-deploy)"
  ok "Done."
  exit 0
fi

log "Ensuring $DEPLOY_PATH exists on VPS..."
ssh -T "${VPS_USER}@${VPS_HOST}" "mkdir -p '${DEPLOY_PATH}'" \
  || fail "Failed to create $DEPLOY_PATH on VPS"

log "Copying compose + env template to VPS..."
scp -q docker-compose.prod.yml ".env.example" "${VPS_USER}@${VPS_HOST}:${DEPLOY_PATH}/" \
  || fail "Failed to copy files to VPS"

log "Deploying on $VPS_HOST..."
ssh -T "${VPS_USER}@${VPS_HOST}" \
  "DEPLOY_PATH='${DEPLOY_PATH}' \
   COMPOSE_PROJECT_NAME='${COMPOSE_PROJECT_NAME}' \
   CONTAINER_PREFIX='${CONTAINER_PREFIX}' \
   IMAGE_REPO='${IMAGE_REPO}' \
   IMAGE_TAG='${IMAGE_SHA_TAG}' \
   HOST_PORT='${HOST_PORT}' \
   PAYLOADS_VOLUME='${PAYLOADS_VOLUME}' \
   PUBLIC_URL='${PUBLIC_URL}' \
   bash -s" <<'REMOTE'
set -e
cd "$DEPLOY_PATH"

# Pre-create the external payloads volume if missing (no-op if exists).
docker volume create "$PAYLOADS_VOLUME" >/dev/null

# Bootstrap .env on first deploy. The operator must paste a TUNNEL_TOKEN
# before the tunnel container can start — we detect that case and warn loudly.
if [ ! -f .env ]; then
  cp .env.example .env
  echo "[remote] Created .env from .env.example."
fi

export COMPOSE_PROJECT_NAME CONTAINER_PREFIX IMAGE_REPO IMAGE_TAG \
       HOST_PORT PAYLOADS_VOLUME

echo "[remote] Pulling ${IMAGE_REPO}:${IMAGE_TAG}..."
docker pull "${IMAGE_REPO}:${IMAGE_TAG}"

echo "[remote] (Re)creating container..."
docker compose -f docker-compose.prod.yml up -d

echo "[remote] Waiting for mcp container to be running..."
for i in $(seq 1 15); do
  state=$(docker inspect -f '{{.State.Status}}' "${CONTAINER_PREFIX}" 2>/dev/null || echo "missing")
  [ "$state" = "running" ] && break
  sleep 2
done
[ "$state" != "running" ] && echo "[remote] WARNING: mcp container never started (state=$state)"

echo "[remote] Local health check (inside VPS):"
curl -sf "http://127.0.0.1:${HOST_PORT}/health" \
  && echo \
  || echo "[remote] WARNING: local health check failed"

echo "[remote] Deploy complete at $(date)"
REMOTE

ok "Deployed to $VPS_HOST · sha=$GIT_SHA"
log "→ $PUBLIC_URL"

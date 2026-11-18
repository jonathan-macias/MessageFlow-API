#!/usr/bin/env bash
#
# Despliega MessageFlow en la Orange Pi. Lo ejecuta el runner self-hosted desde
# .github/workflows/deploy.yml, pero también se puede lanzar a mano:
#
#   bash deploy/deploy.sh sha-abc1234
#
# Pasos: copia el compose → backup de la BD → pull → migraciones → API nueva →
# healthcheck. Si el healthcheck falla vuelve a la versión anterior y termina con error.

set -Eeuo pipefail

NEW_TAG="${1:-}"
APP_DIR="${APP_DIR:-/opt/messageflow}"
HEALTH_URL="${HEALTH_URL:-http://127.0.0.1:5080/health}"
HEALTH_TIMEOUT="${HEALTH_TIMEOUT:-120}"
BACKUPS_TO_KEEP="${BACKUPS_TO_KEEP:-10}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ENV_FILE="$APP_DIR/.env"

log()  { printf '\n\033[1;34m▶ %s\033[0m\n' "$*"; }
fail() { printf '\n\033[1;31m✖ %s\033[0m\n' "$*" >&2; exit 1; }

[[ "$NEW_TAG" =~ ^[A-Za-z0-9_.-]+$ ]] || fail "Uso: deploy.sh <image_tag>  (recibido: '${NEW_TAG}')"
[[ -d "$APP_DIR" ]]  || fail "No existe $APP_DIR. Ver nota '03 - Preparar la Orange Pi'."
[[ -f "$ENV_FILE" ]] || fail "No existe $ENV_FILE. Copia deploy/.env.example y rellénalo."

compose() {
  docker compose --project-directory "$APP_DIR" -f "$APP_DIR/docker-compose.prod.yml" "$@"
}

current_tag() { grep -E '^IMAGE_TAG=' "$ENV_FILE" | tail -n1 | cut -d= -f2- || true; }

set_tag() {
  if grep -qE '^IMAGE_TAG=' "$ENV_FILE"; then
    sed -i "s/^IMAGE_TAG=.*/IMAGE_TAG=$1/" "$ENV_FILE"
  else
    printf '\nIMAGE_TAG=%s\n' "$1" >> "$ENV_FILE"
  fi
}

wait_healthy() {
  local deadline=$(( SECONDS + HEALTH_TIMEOUT ))
  until curl -fsS --max-time 5 "$HEALTH_URL" > /dev/null 2>&1; do
    (( SECONDS >= deadline )) && return 1
    sleep 3
  done
}

# ── 1. Archivos del stack ─────────────────────────────────────────────────────
log "Copiando compose e initdb a $APP_DIR"
install -m 644 "$SCRIPT_DIR/docker-compose.prod.yml" "$APP_DIR/docker-compose.prod.yml"
mkdir -p "$APP_DIR/initdb" "$APP_DIR/backups"
install -m 644 "$SCRIPT_DIR"/initdb/*.sql "$APP_DIR/initdb/"

PREV_TAG="$(current_tag)"
log "Versión actual: ${PREV_TAG:-<ninguna>}  →  nueva: $NEW_TAG"

# ── 2. Backup antes de migrar (solo si Postgres ya está corriendo) ────────────
if [[ -n "$(compose ps -q --status running postgres 2>/dev/null)" ]]; then
  BACKUP="$APP_DIR/backups/pre-${NEW_TAG}-$(date +%Y%m%d-%H%M%S).sql.gz"
  log "Backup de la base de datos → $BACKUP"
  compose exec -T postgres pg_dump -U messageflow -d messageflow | gzip > "$BACKUP"
  ls -1t "$APP_DIR"/backups/pre-*.sql.gz | tail -n +$(( BACKUPS_TO_KEEP + 1 )) | xargs -r rm -f
fi

# ── 3. Descargar imágenes nuevas ──────────────────────────────────────────────
# IMAGE_TAG en el entorno tiene prioridad sobre .env: hasta el paso 5 el .env
# sigue apuntando a la versión anterior, así un fallo aquí no deja nada a medias.
export IMAGE_TAG="$NEW_TAG"
log "Descargando imágenes"
compose --profile migrate pull api migrate postgres evolution-api \
  || fail "No se pudieron descargar las imágenes de $NEW_TAG. Nada ha cambiado."

# ── 4. Infraestructura + migraciones ──────────────────────────────────────────
log "Levantando postgres y evolution-api"
compose up -d --wait postgres
compose up -d evolution-api

log "Aplicando migraciones EF Core"
compose --profile migrate run --rm migrate \
  || fail "Las migraciones fallaron. La API sigue en ${PREV_TAG:-<ninguna>}. Backup en $APP_DIR/backups."

# ── 5. API nueva + healthcheck ────────────────────────────────────────────────
log "Levantando API $NEW_TAG"
set_tag "$NEW_TAG"
compose up -d api

log "Esperando healthcheck en $HEALTH_URL (máx ${HEALTH_TIMEOUT}s)"
if wait_healthy; then
  printf '%s\n' "$NEW_TAG" > "$APP_DIR/.last_successful_tag"
  printf '%s %s\n' "$(date -Is)" "$NEW_TAG" >> "$APP_DIR/deploy-history.log"
  log "Limpiando imágenes antiguas (>10 días y sin uso)"
  docker image prune -af --filter "until=240h" > /dev/null || true
  log "✅ Desplegado $NEW_TAG"
  exit 0
fi

# ── 6. Rollback automático ────────────────────────────────────────────────────
printf '\n\033[1;31m✖ Healthcheck fallido. Últimos logs de la API:\033[0m\n'
compose logs --tail 80 api || true

if [[ -n "$PREV_TAG" && "$PREV_TAG" != "$NEW_TAG" ]]; then
  log "Rollback a $PREV_TAG"
  set_tag "$PREV_TAG"
  export IMAGE_TAG="$PREV_TAG"
  compose up -d api
  wait_healthy && log "Rollback OK: la API vuelve a estar en $PREV_TAG" \
               || printf '\033[1;31mEl rollback tampoco pasa el healthcheck: revisar a mano.\033[0m\n'
fi
fail "Deploy de $NEW_TAG fallido."

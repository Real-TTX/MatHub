#!/usr/bin/env sh
# Baut den MatHub-Container neu und startet den lokalen Stack. "--clean" löscht vorher die Volumes.
set -e
cd "$(dirname "$0")/.."
if [ "$1" = "--clean" ]; then
  docker compose -f docker-compose.local.yml down -v --remove-orphans
fi
docker compose -f docker-compose.local.yml up -d --build --remove-orphans
echo "MatHub läuft auf http://localhost:4910 (pgAdmin: http://localhost:4911)"

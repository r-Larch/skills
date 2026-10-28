#!/usr/bin/env bash
# Writes this server's .env once: its own random tokens and passwords, generated here and never
# copied from another machine. Refuses to overwrite an existing .env. Prints no secret.
#   ./gen-env.sh <host> [version]      e.g. ./gen-env.sh workstate.dikaia.ai 0.9.0
set -euo pipefail

cd "$(dirname "$0")"
host=${1:?usage: gen-env.sh <host> [version]}
version=${2:-0.9.0}

if [[ -e .env ]]; then echo ".env exists; leaving it alone" >&2; exit 1; fi

subnet=$(docker network inspect web -f '{{range .IPAM.Config}}{{.Subnet}} {{end}}' | awk '{print $1}')
if [[ -z "$subnet" ]]; then echo "no subnet on docker network 'web'; is multi-deploy's Traefik up?" >&2; exit 1; fi

secret() { openssl rand -hex 32; }

umask 077
cat > .env <<EOF
WORKSTATE_VERSION=$version
WORKSTATE_HOST=$host
WORKSTATE_PUBLIC_URL=https://$host
WORKSTATE_TRUSTED_PROXIES=$subnet

WORKSTATE_WORKER_TOKEN=worker-$(secret)
WORKSTATE_ORCHESTRATOR_TOKEN=orchestrator-$(secret)
WORKSTATE_CONTROLLER_TOKEN=controller-$(secret)

WORKSTATE_DASHBOARD_USERNAME=operator
WORKSTATE_DASHBOARD_PASSWORD=$(secret)

POSTGRES_DB=workstate
POSTGRES_USER=workstate
POSTGRES_PASSWORD=$(secret)
EOF

echo "wrote .env (mode 600): version $version, https://$host, trusted proxies $subnet"
echo "read the dashboard password on this server with: grep ^WORKSTATE_DASHBOARD_PASSWORD .env"

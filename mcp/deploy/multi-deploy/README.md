# WorkState on a multi-deploy server

Runs the published image `ghcr.io/r-larch/workstate-api` behind
[multi-deploy](https://github.com/r-Larch/multi-deploy)'s Traefik, with HTTPS from Let's Encrypt.
The image is the same one a laptop runs. Only `.env` differs: the public origin, the trusted proxy
and this server's own secrets.

## First install (as root on the server)

```bash
mkdir -p /opt/multi-deploy/apps/workstate
cd /opt/multi-deploy/apps/workstate
for f in compose.yml app.env gen-env.sh backup.sh workstate-backup.service workstate-backup.timer; do
  curl -fsSLO "https://raw.githubusercontent.com/r-Larch/skills/master/mcp/deploy/multi-deploy/$f"
done
chmod +x gen-env.sh backup.sh

./gen-env.sh workstate.dikaia.ai 0.9.0   # random secrets, generated here; prints none of them
app deploy workstate                     # pulls the image, starts db + api, Traefik picks it up

cp workstate-backup.service workstate-backup.timer /etc/systemd/system/
systemctl daemon-reload && systemctl enable --now workstate-backup.timer
./backup.sh                              # the first dump, and a check that backups work
```

DNS for the host must point at the server before `app deploy`, or Let's Encrypt cannot issue the
certificate.

## Check it

```bash
curl -s -o /dev/null -w "%{http_code}\n" https://workstate.dikaia.ai/health        # 200
curl -s -D - -o /dev/null -X POST https://workstate.dikaia.ai/mcp \
  -H "Content-Type: application/json" -H "Accept: application/json, text/event-stream" \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize"}' | grep -i www-auth
# WWW-Authenticate: Bearer resource_metadata="https://workstate.dikaia.ai/.well-known/oauth-protected-resource/mcp"
```

If the challenge names `http://` instead of `https://`, Traefik's address is not in
`WORKSTATE_TRUSTED_PROXIES`. Compare it with `docker network inspect web`.

## Upgrade

```bash
cd /opt/multi-deploy/apps/workstate
./backup.sh
sed -i 's/^WORKSTATE_VERSION=.*/WORKSTATE_VERSION=<new>/' .env
app deploy workstate
```

Every connected Claude Code session then needs `/mcp` → Reconnect: MCP sessions live in the
process.

The server accepts no static role token (doc 08 §4): `compose.yml` passes none, and `gen-env.sh`
writes none. A `.env` from before 1.0.0 still holds `WORKSTATE_{WORKER,ORCHESTRATOR,CONTROLLER}_TOKEN`
lines; they are unused, and removing them prints nothing:

```bash
sed -i '/^WORKSTATE_\(WORKER\|ORCHESTRATOR\|CONTROLLER\)_TOKEN=/d' .env
```

## Restore

```bash
docker exec -i workstate-db-1 pg_restore -U workstate -d workstate --clean --if-exists < /var/backups/workstate/<file>.dump
docker restart workstate-api-1
```

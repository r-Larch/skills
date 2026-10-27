#!/usr/bin/env bash
# workstate-setup.sh - install, start, register and prove a local WorkState server.
# Twin of workstate-setup.ps1: same steps, same flags, same status lines. Change both together.
#
# Every line this prints is safe to show a model: tokens and passwords stay in the .env file and in
# the harness's own config, and nothing here echoes them (SKILL.md "What the model never sees").
set -euo pipefail

usage() {
    cat <<'EOF'
usage: workstate-setup.sh [options]
       workstate-setup.sh --backup <file>  [--home-dir <dir> | --clone <path>] [--project <name>]
       workstate-setup.sh --restore <file> [--home-dir <dir> | --clone <path>] [--project <name>]

  --clone <path>          use a clone of github.com/r-Larch/skills: <path>/mcp/compose.yml (builds
                          the image) and <path>/mcp/.env. Without it: the WorkState home.
  --home-dir <dir>        the WorkState home (default ~/.workstate): compose.yml (pulls the
                          published image) and .env
  --auth <oauth|token>    how the registration authenticates. oauth: no token stored, the user signs
                          in on first use (/mcp -> Authenticate). token: a role's bearer token. Default:
                          oauth when the server offers it (its OAuth metadata answers 200), else token
  --role <role>           orchestrator (default) | controller | worker - whose token to register
                          (token only: an OAuth sign-in always gets the controller toolset)
  --name <name>           registration name (default workstate)
  --agent <session>       register at LOCAL scope (this directory's project only) with the header
                          X-WorkState-Agent: <session>, so sessions here are recorded by that name
  --port <port>           host port, written only into a NEW .env (default 5290)
  --project <name>        compose project (default workstate)
  --image-version <tag>   image tag for the pull-only compose (default latest)
  --update                pull the image again (home) or rebuild it (clone) before starting
  --force                 replace an existing registration of the same name
  --session-names         re-register an existing registration with the session helper (a new one
                          gets it anyway), keeping the role its token has unless --role is given
  --dry-run              do everything except write the registration; show it (a token masked)
  --backup <file>         pg_dump -Fc the database to <file>, and stop
  --restore <file>        restore <file> over the database (after a safety backup), and stop
EOF
}

die() { printf '%s\n' "$1" >&2; exit 1; }
status() { printf '%-10s%s\n' "$1" "$2"; }

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
clone=""; home_dir=""; role="orchestrator"; name="workstate"; agent=""; port="5290"; project="workstate"
image_version=""; update=0; force=0; session_names=0; role_given=0; dry_run=0; backup=""; restore=""
auth=""

while [ $# -gt 0 ]; do
    case "$1" in
        --clone) clone="${2:?--clone needs a path}"; shift 2 ;;
        --home-dir) home_dir="${2:?--home-dir needs a directory}"; shift 2 ;;
        --auth) auth="${2:?--auth needs oauth or token}"; shift 2 ;;
        --role) role="${2:?--role needs a value}"; role_given=1; shift 2 ;;
        --name) name="${2:?--name needs a value}"; shift 2 ;;
        --agent) agent="${2:?--agent needs a session name}"; shift 2 ;;
        --port) port="${2:?--port needs a value}"; shift 2 ;;
        --project) project="${2:?--project needs a value}"; shift 2 ;;
        --image-version) image_version="${2:?--image-version needs a tag}"; shift 2 ;;
        --update) update=1; shift ;;
        --force) force=1; shift ;;
        --session-names) session_names=1; shift ;;
        --dry-run) dry_run=1; shift ;;
        --backup) backup="${2:?--backup needs a file}"; shift 2 ;;
        --restore) restore="${2:?--restore needs a file}"; shift 2 ;;
        -h|--help) usage; exit 0 ;;
        *) usage >&2; die "unknown argument: $1" ;;
    esac
done

case "$role" in orchestrator|controller|worker) ;; *) die "--role must be orchestrator, controller or worker" ;; esac
case "$auth" in ''|oauth|token) ;; *) die "--auth must be oauth or token" ;; esac
[ "$auth" = "oauth" ] && [ "$role_given" -eq 1 ] && die "--role with --auth oauth: an OAuth sign-in is the dashboard user with the controller toolset; --role needs --auth token"
case "$port" in ''|*[!0-9]*) die "--port must be a number" ;; esac
[ -n "$clone" ] && [ -n "$home_dir" ] && die "--clone and --home-dir are two different installs; pass one"

env_value() { # the value of key $1 in the env file, without printing it anywhere
    sed -n "s/^$1=//p" "$env_file" | tail -n 1 | tr -d '\r'
}

find_container() { # $1 = compose service
    docker ps -q --filter "label=com.docker.compose.project=$project" --filter "label=com.docker.compose.service=$1" | head -n 1
}

command -v docker >/dev/null 2>&1 || die "docker    not found: install Docker Desktop (or Docker Engine) first"
docker info >/dev/null 2>&1 || die "docker    not reachable: start Docker Desktop (Linux: sudo systemctl start docker) and run this again"

# a. where the install lives
if [ -n "$clone" ]; then
    [ -f "$clone/mcp/compose.yml" ] || die "home      $clone/mcp/compose.yml not found: --clone must be the repo root"
    clone="$(cd "$clone" && pwd)"
    compose_file="$clone/mcp/compose.yml"
    env_file="$clone/mcp/.env"
    if ! git -C "$clone" check-ignore -q mcp/.env 2>/dev/null; then
        die "home      $clone/mcp/.env is NOT gitignored: stop and tell the user; a git add would publish the tokens"
    fi
    mode="clone"
else
    [ -n "$home_dir" ] || home_dir="$HOME/.workstate"
    mkdir -p "$home_dir"
    home_dir="$(cd "$home_dir" && pwd)"
    compose_file="$home_dir/compose.yml"
    env_file="$home_dir/.env"
    mode="home"
fi
base_dir="$(dirname "$env_file")"

dc() { docker compose -p "$project" -f "$compose_file" --env-file "$env_file" "$@"; }

if [ -n "$backup" ] || [ -n "$restore" ]; then
    [ -n "$backup" ] && [ -n "$restore" ] && die "--backup and --restore: pass one"
    db="$(find_container db)"
    [ -n "$db" ] || die "backup    no running db container in compose project '$project'"
    dump_to() { # $1 = host file
        docker exec "$db" sh -c 'pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB" -Fc -f /tmp/workstate.dump' >/dev/null
        docker cp "$db:/tmp/workstate.dump" "$1" >/dev/null
        docker exec "$db" rm -f /tmp/workstate.dump
    }
    if [ -n "$backup" ]; then
        mkdir -p "$(dirname "$backup")"
        dump_to "$backup"
        status "backup" "wrote $backup ($(wc -c <"$backup" | tr -d ' ') bytes, pg_dump -Fc of project '$project')"
        exit 0
    fi
    [ -f "$restore" ] || die "restore   $restore not found"
    mkdir -p "$base_dir/backups"
    safety="$base_dir/backups/pre-restore-$(date +%Y%m%d-%H%M%S).dump"
    dump_to "$safety"
    status "backup" "safety copy of the current database: $safety"
    api="$(find_container api)"
    [ -n "$api" ] && docker stop "$api" >/dev/null
    docker cp "$restore" "$db:/tmp/workstate-restore.dump" >/dev/null
    rc=0
    docker exec "$db" sh -c 'pg_restore -U "$POSTGRES_USER" -d "$POSTGRES_DB" --clean --if-exists --no-owner /tmp/workstate-restore.dump' >/dev/null 2>"$base_dir/backups/restore.log" || rc=$?
    docker exec "$db" rm -f /tmp/workstate-restore.dump
    [ -n "$api" ] && docker start "$api" >/dev/null
    [ "$rc" -eq 0 ] || die "restore   pg_restore exited $rc - see $base_dir/backups/restore.log; the safety copy is $safety"
    status "restore" "restored $restore into project '$project'; api restarted"
    exit 0
fi

if [ "$mode" = "home" ]; then
    if [ ! -f "$compose_file" ]; then
        cp "$script_dir/compose.yml" "$compose_file"
        status "home" "$home_dir (pull-only compose written)"
    elif ! cmp -s "$script_dir/compose.yml" "$compose_file"; then
        cp "$script_dir/compose.yml" "$compose_file"
        status "home" "$home_dir (pull-only compose updated to this skill's version)"
    else
        status "home" "$home_dir (pull-only compose)"
    fi
else
    status "home" "$clone (clone: builds mcp/Dockerfile)"
fi

# b. the .env - written once, never overwritten
random_hex() { od -An -N32 -tx1 /dev/urandom | tr -d ' \n'; }
if [ -f "$env_file" ]; then
    status "env" "kept $env_file (exists; never overwritten)"
elif [ -n "$(docker volume ls -q --filter "name=^${project}_workstate-db-data\$")" ]; then
    die "env       $env_file is missing but project '$project' already has a database volume, created with another .env: pass --clone <path> for that install, or copy its .env here, and run again"
else
    (
        umask 077
        cat >"$env_file" <<EOF
# Written by workstate-setup. Local-only throwaway credentials for a loopback service; never commit.
WORKSTATE_WORKER_TOKEN=local-worker-$(random_hex)
WORKSTATE_ORCHESTRATOR_TOKEN=local-orchestrator-$(random_hex)
WORKSTATE_CONTROLLER_TOKEN=local-controller-$(random_hex)
WORKSTATE_READER_TOKEN=

WORKSTATE_DASHBOARD_USERNAME=operator
WORKSTATE_DASHBOARD_PASSWORD=local-dashboard-$(random_hex)

WORKSTATE_API_PORT=$port
WORKSTATE_BIND=127.0.0.1

POSTGRES_DB=workstate
POSTGRES_USER=workstate
POSTGRES_PASSWORD=local-$(random_hex)
EOF
    )
    chmod 600 "$env_file" 2>/dev/null || true
    status "env" "wrote $env_file: 3 tokens, dashboard and Postgres passwords (values not shown, mode 600)"
fi

# the session helper - copied next to the .env, because the plugin cache path changes per version
helper_file="$base_dir/session-header.mjs"
cmp -s "$script_dir/session-header.mjs" "$helper_file" 2>/dev/null || cp "$script_dir/session-header.mjs" "$helper_file"
has_node=0
if command -v node >/dev/null 2>&1; then
    has_node=1
    status "helper" "$helper_file (names each session; needs node, found)"
else
    status "helper" "$helper_file copied, but node is not on PATH: registering without it, so sessions stay unnamed"
fi

port="$(env_value WORKSTATE_API_PORT)"; port="${port:-5290}"
base_url="http://127.0.0.1:$port"

# c. start and wait for /health
[ -n "$image_version" ] && export WORKSTATE_VERSION="$image_version"
up_log="$(mktemp)"
trap 'rm -f "$up_log"' EXIT
if [ "$update" -eq 1 ]; then
    if [ "$mode" = "home" ]; then dc pull api >"$up_log" 2>&1 || { cat "$up_log" >&2; die "start     docker compose pull failed (output above)"; }
    else up_args=(--build); fi
fi
dc up -d "${up_args[@]+"${up_args[@]}"}" >"$up_log" 2>&1 || { cat "$up_log" >&2; die "start     docker compose up failed (output above)"; }
health=""
for _ in $(seq 1 90); do
    health="$(curl -s -m 2 -o /dev/null -w '%{http_code}' "$base_url/health" || true)"
    [ "$health" = "200" ] && break
    sleep 2
done
[ "$health" = "200" ] || die "start     $base_url/health never answered 200 (last: ${health:-none}); docker compose -p $project logs api"
status "start" "project '$project' up, $base_url/health 200"

# d. register - the token goes from the .env straight into the harness's config; with oauth none is stored
mcp_url="$base_url/mcp"
prm_check() { # $1 = url; prints what it answered, and succeeds only for an OAuth protected-resource document
    local reply code
    reply="$(curl -s -m 5 -w '\n%{http_code}' "$1" || true)"
    code="$(printf '%s\n' "$reply" | tail -n 1)"
    # 200 alone proves nothing: the dashboard answers unknown paths with its index.html
    if [ "$code" = "200" ] && printf '%s' "$reply" | grep -q '"authorization_servers"'; then printf '200'; return 0; fi
    if [ "$code" = "200" ]; then printf '200 without authorization_servers'; else printf '%s' "${code:-nothing}"; fi
    return 1
}
# WS_SETUP_PRM_URL is a test seam: point it at a 404 to see the token fallback without stopping the server.
prm_url="${WS_SETUP_PRM_URL:-$base_url/.well-known/oauth-protected-resource/mcp}"
offers_oauth=0
prm="$(prm_check "$prm_url")" && offers_oauth=1
existing=0
scope="user"
[ -n "$agent" ] && scope="local"
get_out=""
if ! command -v claude >/dev/null 2>&1; then
    [ "$dry_run" -eq 1 ] || die "register  the claude CLI is not on PATH: install Claude Code, or run this where it is"
# Read here and never printed: SKILL.md forbids the model this command because it shows the header.
elif get_out="$(claude mcp get "$name" 2>&1)"; then existing=1
elif ! printf '%s' "$get_out" | grep -q "No MCP server named"; then
    die "register  claude mcp get $name failed; run claude mcp list to see why"
fi
if [ "$existing" -eq 1 ] && [ "$scope" = "local" ] && ! printf '%s' "$get_out" | grep -q "Scope: Local"; then existing=0; fi
existing_auth="oauth"
printf '%s' "$get_out" | grep -q "Bearer " && existing_auth="token"

# which auth: a kept registration keeps its own; else --auth, --role, the re-registered one's, the server's offer
keep=0
[ "$existing" -eq 1 ] && [ "$force" -eq 0 ] && [ "$session_names" -eq 0 ] && keep=1
if [ "$keep" -eq 1 ]; then
    why="the existing '$name' registration uses $existing_auth, kept"
    [ -n "$auth" ] && [ "$auth" != "$existing_auth" ] && why="$why (--force replaces it with $auth)"
    auth="$existing_auth"
elif [ -n "$auth" ]; then why="--auth $auth"
elif [ "$role_given" -eq 1 ]; then auth="token"; why="--role $role needs a token"
elif [ "$existing" -eq 1 ]; then auth="$existing_auth"; why="the existing '$name' registration uses $existing_auth"
elif [ "$offers_oauth" -eq 1 ]; then auth="oauth"; why="the server offers OAuth sign-in ($prm_url 200)"
else auth="token"; why="the server has no OAuth sign-in ($prm_url answered $prm): an image older than OAuth; --update gets a newer one"
fi
if [ "$auth" = "oauth" ] && [ "$keep" -eq 0 ] && [ "$offers_oauth" -eq 0 ]; then
    die "auth      --auth oauth, but $prm_url answered $prm: this server predates OAuth sign-in; run with --update (home: pulls the image, clone: rebuilds it), or pass --auth token"
fi
status "auth" "$auth: $why"

upper() { printf '%s' "$1" | tr '[:lower:]' '[:upper:]'; }
# --session-names on an existing registration keeps the role its token already has.
if [ "$auth" = "token" ] && [ "$existing" -eq 1 ] && [ "$session_names" -eq 1 ] && [ "$role_given" -eq 0 ]; then
    for candidate in controller orchestrator worker; do
        candidate_token="$(env_value "WORKSTATE_$(upper "$candidate")_TOKEN")"
        if [ -n "$candidate_token" ] && printf '%s' "$get_out" | grep -qF "Bearer $candidate_token"; then role="$candidate"; break; fi
    done
fi
token=""
if [ "$auth" = "token" ]; then
    token="$(env_value "WORKSTATE_$(upper "$role")_TOKEN")"
    [ -n "$token" ] || die "env       $env_file has no WORKSTATE_$(upper "$role")_TOKEN"
fi

json_str() { printf '"%s"' "$(printf '%s' "$1" | sed 's/\\/\\\\/g; s/"/\\"/g')"; }
helper_path="$helper_file"
command -v cygpath >/dev/null 2>&1 && helper_path="$(cygpath -m "$helper_file")"
registration() { # $1 = the bearer token, or *** for display; oauth stores none and ignores it
    local headers=""
    [ "$auth" = "token" ] && headers="\"Authorization\":$(json_str "Bearer $1")"
    [ -n "$agent" ] && headers="$headers${headers:+,}\"X-WorkState-Agent\":$(json_str "$agent")"
    local entry="{\"type\":\"http\",\"url\":$(json_str "$mcp_url")"
    [ -n "$headers" ] && entry="$entry,\"headers\":{$headers}"
    [ "$has_node" -eq 1 ] && entry="$entry,\"headersHelper\":$(json_str "node \"$helper_path\"")"
    printf '%s}' "$entry"
}
shown="claude mcp add-json --scope $scope $name '$(registration '***')'"

if [ "$keep" -eq 1 ]; then
    status "register" "kept the existing '$name' registration (pass --session-names to add the session helper, --force to replace it)"
elif [ "$dry_run" -eq 1 ]; then
    status "register" "dry run, would run: $shown"
else
    if [ "$existing" -eq 1 ]; then
        claude mcp remove "$name" --scope "$scope" >/dev/null 2>&1 || die "register  claude mcp remove $name --scope $scope failed (is it registered in another scope?)"
    fi
    claude mcp add-json --scope "$scope" "$name" "$(registration "$token")" >/dev/null 2>&1 \
        || die "register  claude mcp add-json failed: $shown"
    helper_note=""; [ "$has_node" -eq 1 ] && helper_note=", session helper"
    if [ "$auth" = "oauth" ]; then
        status "register" "'$name' -> $mcp_url with OAuth sign-in${agent:+, agent $agent}$helper_note ($scope scope; no token stored)"
    else
        status "register" "'$name' -> $mcp_url as $role${agent:+, agent $agent}$helper_note ($scope scope; token stored by Claude Code, not shown)"
    fi
fi

# the shadow check - a local or project entry of the same name wins over the user one; removing it is the user's call
if [ "$has_node" -eq 1 ]; then
    here="$PWD"
    command -v cygpath >/dev/null 2>&1 && here="$(cygpath -m "$PWD")"
    finder_args=("$script_dir/find-shadows.mjs" --name "$name" --dir "$here")
    [ "$scope" = "local" ] && finder_args+=(--own "$here")
    shadows="$(node "${finder_args[@]}" 2>/dev/null || true)"
    found=(); unchecked=0
    while IFS= read -r line; do
        case "$line" in
            '') ;;
            'not checked'*) status "shadows" "$line"; unchecked=1 ;;
            *) found+=("$line") ;;
        esac
    done <<<"$shadows"
    if [ "${#found[@]}" -gt 0 ]; then
        status "shadows" "${#found[@]} '$name' entry(s) win over the user registration (local > project > user), so sessions there miss it and its session helper; this script removes nothing - run the command shown yourself:"
        for line in "${found[@]}"; do printf '          %s\n' "$line"; done
    elif [ "$unchecked" -eq 0 ]; then
        status "shadows" "none: no local or project '$name' entry overrides it"
    fi
else
    status "shadows" "not checked (needs node)"
fi

# e. prove - oauth: the server asks for sign-in the way Claude Code discovers it; token: count what it lists
if [ "$auth" = "oauth" ]; then
    reply="$(curl -s -m 10 -o /dev/null -D - -w '%{http_code}' -X POST "$mcp_url" -H "Content-Type: application/json" \
        -H "Accept: application/json, text/event-stream" -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}' || true)"
    code="$(printf '%s\n' "$reply" | tail -n 1)"
    metadata="$(printf '%s' "$reply" | tr -d '\r' | sed -n 's/^[Ww][Ww][Ww]-[Aa]uthenticate:.*resource_metadata="\([^"]*\)".*/\1/p' | head -n 1)"
    [ "$code" = "401" ] || die "prove     POST $mcp_url without a token answered ${code:-nothing}, not 401: this server does not ask for sign-in; docker compose -p $project logs api"
    [ -n "$metadata" ] || die "prove     POST $mcp_url answered 401 without resource_metadata=: Claude Code cannot find the sign-in; an image older than OAuth (run with --update)"
    metadata_code="$(prm_check "$metadata")" || die "prove     $metadata answered $metadata_code, not the OAuth metadata: Claude Code cannot discover the sign-in; docker compose -p $project logs api"
    status "prove" "POST /mcp without a token: 401 with resource_metadata=$metadata, which answers 200 - Claude Code finds the sign-in from there"
else
    body="$(curl -s -m 10 -X POST "$mcp_url" -H "Content-Type: application/json" -H "Accept: application/json, text/event-stream" \
        -H "Authorization: Bearer $token" -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}' || true)"
    count="$(printf '%s' "$body" | grep -o '"name":"work_[a-z_]*"' | sort -u | wc -l | tr -d ' ')"
    case "$count" in
        19) implied="orchestrator/controller" ;;
        6) implied="worker" ;;
        0) die "prove     tools/list returned no work_* tool for the $role token: the token or the server is wrong; docker compose -p $project logs api" ;;
        *) implied="unexpected - an image older or newer than these skills (run with --update)" ;;
    esac
    status "prove" "$count work_* tools for the $role token: $implied"
fi

# f. what the human needs next
status "dashboard" "$base_url - sign in with WORKSTATE_DASHBOARD_USERNAME / _PASSWORD from $env_file"
if [ "$auth" = "oauth" ]; then
    status "next" "start a new Claude Code session, run /mcp, pick '$name', Authenticate, sign in with that dashboard user and Approve: the work_* tools appear there, not in this one"
else
    status "next" "start a new Claude Code session: the work_* tools appear there, not in this one"
fi

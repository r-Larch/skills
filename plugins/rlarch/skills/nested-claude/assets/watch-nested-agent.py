"""
Watchdog for a nested agent, shaped for the Monitor tool: every line it prints on stdout becomes one
notification, so it prints ONLY on a signal - never a running commentary.

    Monitor(command: "python -u <this>/watch-nested-agent.py e62a5e14", persistent: true,
            description: "fix-B-02 watchdog")

Signals, each emitted once per occurrence rather than once per poll:

    BLOCKED    the agent is parked on a permission prompt and no human is at its keyboard.
               `claude agents --json` reports status=waiting with waitingFor set. This is the one
               that matters: a blocked agent looks exactly like a busy agent from the outside.
    STALL      the transcript has not grown for --stall minutes while the agent still claims to be
               working. A hung tool call, or a turn that died without exiting.
    STATUS     status/state changed (busy -> idle, idle -> done).
    GONE       the agent is no longer listed - it exited, or was stopped. Ends the watch.
    HEARTBEAT  every --beat minutes, elapsed time plus the tail of its last assistant message, so a
               long run stays legible without attaching. Set --beat 0 to switch it off.

Coverage is the point: a watch that only greps for success is silent through a crashloop, and
silence is indistinguishable from progress.
"""
import argparse, glob, io, json, os, subprocess, sys, time

def rows():
    try:
        out = subprocess.run(["claude", "agents", "--json"], capture_output=True, text=True, timeout=60)
        return json.loads(out.stdout or "[]")
    except Exception:
        return None          # a transient failure is not a dead agent; keep watching

def transcript(session_id):
    hits = glob.glob(os.path.expanduser(f"~/.claude/projects/*/{session_id}.jsonl"))
    return hits[0] if hits else None

def last_text(path, limit=220):
    try:
        with io.open(path, encoding="utf-8", errors="replace") as f:
            lines = f.readlines()[-400:]
    except Exception as e:
        # Say so rather than reporting an empty tail: a silent "" here read as "the agent has said
        # nothing", which is exactly what a missing `import io` looked like for a whole round.
        return f"(cannot read transcript: {type(e).__name__})"
    for line in reversed(lines):
        try:
            o = json.loads(line)
        except Exception:
            continue          # a half-written last line while the agent is mid-append
        m = o.get("message") or {}
        if m.get("role") != "assistant":
            continue
        c = m.get("content")
        parts = [c] if isinstance(c, str) else [
            x.get("text", "") for x in (c or []) if isinstance(x, dict) and x.get("type") == "text"]
        t = " ".join(p for p in parts if p).strip().replace(chr(10), " ")
        if t:
            return t[:limit]
    return ""

def emit(*a):
    print(*a, flush=True)

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("agent_id")
    ap.add_argument("--poll",  type=int, default=30,  help="seconds between checks")
    ap.add_argument("--beat",  type=int, default=10,  help="heartbeat minutes; 0 disables")
    ap.add_argument("--stall", type=int, default=8,   help="minutes of transcript silence before STALL")
    a = ap.parse_args()

    started = time.time()
    name, tx = a.agent_id, None
    seen_status, blocked_on, last_beat, last_growth, stalled = None, None, started, started, False
    size = -1

    while True:
        rs = rows()
        if rs is not None:
            row = next((r for r in rs if r.get("id") == a.agent_id), None)
            if row is None:
                emit(f"GONE {name} ({a.agent_id}) - no longer listed after {int((time.time()-started)/60)}m. "
                     f"Verify its work; the transcript on disk outlives it.")
                return
            name = row.get("name") or name
            tx = tx or transcript(row.get("sessionId") or "")

            waiting = row.get("waitingFor")
            if waiting and waiting != blocked_on:
                emit(f"BLOCKED {name} - waiting on: {waiting}. No human is attached. "
                     f"Clear it with: claude attach {a.agent_id}")
            blocked_on = waiting

            status = f"{row.get('status')}/{row.get('state')}"
            if seen_status is not None and status != seen_status:
                emit(f"STATUS {name} - {seen_status} -> {status}")
            seen_status = status

        if tx and os.path.exists(tx):
            cur = os.path.getsize(tx)
            if cur != size:
                size, last_growth, stalled = cur, time.time(), False
            elif not stalled and (time.time() - last_growth) > a.stall * 60 and (blocked_on is None):
                stalled = True
                emit(f"STALL {name} - transcript unchanged for {a.stall}m while still listed. "
                     f"Last said: {last_text(tx)}")

        if a.beat and (time.time() - last_beat) > a.beat * 60:
            last_beat = time.time()
            emit(f"HEARTBEAT {name} - {int((time.time()-started)/60)}m, {seen_status}. "
                 f"Last said: {last_text(tx) if tx else '(no transcript yet)'}")

        time.sleep(a.poll)

if __name__ == "__main__":
    main()

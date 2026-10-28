// fake-browser.mjs — stands in for the system browser via AGENTS_BROWSER: GETs the URL and follows
// the redirect to the loopback callback, as a browser whose user approves at once would.
// @ts-check

try {
    await fetch(process.argv[2], { redirect: 'follow' })
} catch {}

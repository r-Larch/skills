// header.mjs — the protocol header prepended to every brief, and the toolset profiles.
//
// Both are copies of what assets/Start-NestedAgent.ps1 holds, so all three launch paths write the
// same brief and grant the same tools; test/parity.test.mjs parses the .ps1 and fails on any drift.
// @ts-check

/**
 * LF whatever this file's checkout endings are, so the brief matches the scripts' byte for byte.
 * @param {string} controller
 * @param {string} name
 */
export function protocolHeader(controller, name) {
    return headerText(controller, name).replace(/\r\n/g, '\n')
}

/** @param {string} controller @param {string} name */
function headerText(controller, name) {
    return `Your controller is the Claude Code session '${controller}'. It started you, it is watching you, and it
is who you report to and ask. If a human attaches to this session they are observing, not directing:
questions go to the controller. SendMessage with to="${controller}", verbatim, brackets included.

Send exactly these, and no progress chatter - the controller reads your transcript for that:

  READY <task>   once, immediately, before any other work.
  BLOCKED <what> the moment you need a decision that is not yours - the task looks wrong, it needs a
                 schema change or a production write, the scope is ambiguous. Then STOP and wait for
                 the reply. Do not guess and carry on.
  DONE <report>  once, at the end: what you changed with file:line, the test that covers it, the
                 exact build and test result, and anything you could not do.

Open every message with your name '${name}'.

If the controller sends you WRAP UP: stop at the next safe point, commit what is finished, and send
DONE saying exactly where you stopped. Do not start anything new.

You have no human at a keyboard. If you hit a permission prompt you cannot clear, do not work around
it and do not ask a peer to act for you - send BLOCKED naming the exact operation, and wait.
`
}

export const READONLY_TOOLS = Object.freeze([
    'Read', 'Glob', 'Grep',
    'Bash(ls:*)', 'Bash(cat:*)', 'Bash(head:*)', 'Bash(tail:*)', 'Bash(grep:*)', 'Bash(rg:*)',
    'Bash(find:*)', 'Bash(wc:*)', 'Bash(sed:-n *)', 'Bash(echo:*)', 'Bash(which:*)',
    'Bash(git status:*)', 'Bash(git diff:*)', 'Bash(git log:*)', 'Bash(git show:*)',
    'Bash(git branch:*)', 'Bash(git rev-parse:*)', 'Bash(git ls-files:*)',
    'PowerShell(Get-ChildItem:*)', 'PowerShell(Get-Content:*)', 'PowerShell(Select-String:*)',
    'PowerShell(Test-Path:*)', 'PowerShell(Get-Command:*)', 'PowerShell(git status:*)',
    'PowerShell(git diff:*)', 'PowerShell(git log:*)',
])

// `git push` is deliberately absent and must stay absent (nested-claude SKILL.md §3).
export const FIX_TOOLS = Object.freeze([
    ...READONLY_TOOLS,
    'Write', 'Edit',
    'Bash(git add:*)', 'Bash(git commit:*)',
    'Bash(dotnet build:*)', 'Bash(dotnet test:*)', 'Bash(pnpm run:*)',
    'PowerShell(git add:*)', 'PowerShell(git commit:*)',
    'PowerShell(dotnet build:*)', 'PowerShell(dotnet test:*)',
])

/** @param {'none'|'readonly'|'fix'} toolset */
export function toolsetProfile(toolset) {
    if (toolset === 'readonly') return [...READONLY_TOOLS]
    if (toolset === 'fix') return [...FIX_TOOLS]
    return []
}

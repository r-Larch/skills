import { For, Show, createEffect, createSignal, on } from 'solid-js'
import * as api from '../api.ts'
import { load } from '../store.ts'

/**
 * The prompt actions that apply to one scope (doc 13). A button opens the whole prompt beside it;
 * Copy puts it on the clipboard for a fresh session. The dashboard never acts on it itself.
 */
export function PromptActions(props: { scope: string }) {
  const prompts = load(() => props.scope, api.prompts)
  const [open, setOpen] = createSignal<string>()
  const shown = () => prompts.data()?.prompts.find((p) => p.key === open())

  return (
    <Show when={(prompts.data()?.prompts.length ?? 0) > 0}>
      <div class="prompt-acts" role="group" aria-label="Prompts for a fresh session">
        <For each={prompts.data()?.prompts}>
          {(p) => (
            <button
              type="button"
              class={`proj-act${open() === p.key ? ' on' : ''}`}
              aria-expanded={open() === p.key}
              title="Show the prompt to copy into a fresh Claude session"
              onClick={() => setOpen(open() === p.key ? undefined : p.key)}
            >
              {p.count === null ? p.label : `${p.label} (${p.count})`}
            </button>
          )}
        </For>
      </div>
      <Show when={shown()}>{(p) => <PromptPanel prompt={p()} onClose={() => setOpen(undefined)} />}</Show>
    </Show>
  )
}

function PromptPanel(props: { prompt: api.Prompt; onClose: () => void }) {
  const [copied, setCopied] = createSignal<'copied' | 'selected'>()
  createEffect(on(() => props.prompt.key, () => setCopied(undefined), { defer: true }))
  let text: HTMLPreElement | undefined

  const select = () => {
    if (text === undefined) return
    const range = document.createRange()
    range.selectNodeContents(text)
    const selection = window.getSelection()
    selection?.removeAllRanges()
    selection?.addRange(range)
  }

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(props.prompt.text)
      setCopied('copied')
    } catch {
      select()
      setCopied('selected')
    }
  }

  return (
    <div class="prompt-panel" role="region" aria-label={`${props.prompt.label} prompt`}>
      <pre class="brief-text" ref={text}>
        {props.prompt.text}
      </pre>
      <div class="confirm-b">
        <button type="button" class="proj-act primary" onClick={() => void copy()}>
          Copy
        </button>
        <button type="button" class="proj-act" onClick={() => props.onClose()}>
          Close
        </button>
        <span class="prompt-note" role="status">
          {copied() === 'copied'
            ? 'Copied. Paste it into a fresh Claude session.'
            : copied() === 'selected'
              ? 'The clipboard is unavailable here; the text is selected, press Ctrl+C.'
              : 'Paste it into a fresh Claude session. Nothing runs from here.'}
        </span>
      </div>
    </div>
  )
}

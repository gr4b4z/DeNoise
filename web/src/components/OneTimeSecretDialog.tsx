import * as Dialog from '@radix-ui/react-dialog';
import { useState } from 'react';

/**
 * Shows a secret exactly once (08 §4 `OneTimeSecretDialog`): the value never appears anywhere else, so the dialog insists on
 * an explicit "I have stored it" before it closes and offers copy buttons for the value and each snippet.
 */
export function OneTimeSecretDialog({ open, title, secret, secretLabel, snippets, onClose }: { open: boolean; title: string; secret: string; secretLabel: string; snippets?: { label: string; code: string }[]; onClose: () => void }) {
  const [copied, setCopied] = useState<string | null>(null);
  const copy = async (key: string, text: string) => {
    try {
      await navigator.clipboard.writeText(text);
      setCopied(key);
      setTimeout(() => setCopied((c) => (c === key ? null : c)), 1500);
    } catch {
      setCopied(null);
    }
  };
  return (
    <Dialog.Root open={open}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 bg-black/30" />
        <Dialog.Content className="fixed top-1/2 left-1/2 max-h-[90dvh] w-[min(720px,calc(100vw-32px))] -translate-x-1/2 -translate-y-1/2 overflow-y-auto rounded-md border border-line bg-surface p-4" style={{ boxShadow: 'var(--shadow-menu)' }} onEscapeKeyDown={(e) => e.preventDefault()} onPointerDownOutside={(e) => e.preventDefault()}>
          <Dialog.Title className="text-md font-semibold">{title}</Dialog.Title>
          <Dialog.Description className="mt-1 text-sm text-ink-2">This is shown once. Alert Hub stores only a hash; if you lose it, rotate the token.</Dialog.Description>
          <div className="mt-3">
            <div className="text-xs text-ink-2">{secretLabel}</div>
            <div className="mt-1 flex items-start gap-2">
              <code className="mono block min-w-0 flex-1 break-all rounded-sm border border-line bg-surface-2 px-2 py-1.5 text-sm" data-testid="one-time-secret">
                {secret}
              </code>
              <button type="button" className="btn btn-sm shrink-0" onClick={() => void copy('secret', secret)}>
                {copied === 'secret' ? 'Copied' : 'Copy'}
              </button>
            </div>
          </div>
          {snippets && snippets.length > 0 && (
            <div className="mt-4 grid gap-3">
              {snippets.map((s) => (
                <div key={s.label}>
                  <div className="flex items-center justify-between text-xs text-ink-2">
                    <span>{s.label}</span>
                    <button type="button" className="btn btn-sm" onClick={() => void copy(s.label, s.code)}>
                      {copied === s.label ? 'Copied' : 'Copy'}
                    </button>
                  </div>
                  <pre className="mono mt-1 overflow-x-auto rounded-sm border border-line bg-surface-2 px-2 py-1.5 text-xs">{s.code}</pre>
                </div>
              ))}
            </div>
          )}
          <div className="mt-4 flex justify-end">
            <button type="button" className="btn btn-primary" onClick={onClose}>
              I have stored it
            </button>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}

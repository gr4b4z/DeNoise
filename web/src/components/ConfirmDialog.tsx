import * as Dialog from '@radix-ui/react-dialog';
import { useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';

export interface ConfirmDialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: string;
  description?: ReactNode;
  /** The verb on the confirm button — the same verb the toast and the timeline will use (08 §0 Copy). */
  verb: string;
  /** When set, a reason textarea is shown and required when `reasonRequired`. */
  withReason?: boolean;
  reasonRequired?: boolean;
  reasonLabel?: string;
  busy?: boolean;
  onConfirm: (reason: string) => void;
}

export function ConfirmDialog({ open, onOpenChange, title, description, verb, withReason, reasonRequired, reasonLabel, busy, onConfirm }: ConfirmDialogProps) {
  const { t } = useTranslation();
  const [reason, setReason] = useState('');
  const handleOpenChange = (next: boolean) => {
    if (!next) setReason('');
    onOpenChange(next);
  };
  const blocked = Boolean(withReason && reasonRequired && reason.trim().length === 0);
  return (
    <Dialog.Root open={open} onOpenChange={handleOpenChange}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 bg-black/30" />
        <Dialog.Content
          className="fixed top-1/2 left-1/2 w-[min(440px,calc(100vw-32px))] -translate-x-1/2 -translate-y-1/2 rounded-md border border-line bg-surface p-4"
          style={{ boxShadow: 'var(--shadow-menu)' }}
          onOpenAutoFocus={(e) => {
            if (withReason) e.preventDefault();
          }}
        >
          <Dialog.Title className="text-md font-semibold">{title}</Dialog.Title>
          {description && <Dialog.Description className="mt-1 text-sm text-ink-2">{description}</Dialog.Description>}
          {withReason && (
            <label className="mt-3 block text-sm">
              <span className="text-ink-2">{reasonLabel ?? t('actions.reason')}</span>
              <textarea
                autoFocus
                className="input mt-1 h-20 resize-none py-1.5"
                value={reason}
                onChange={(e) => setReason(e.target.value)}
                aria-required={reasonRequired}
              />
              {reasonRequired && reason.length === 0 && <span className="text-xs text-ink-2">{t('actions.reasonRequired')}</span>}
            </label>
          )}
          <div className="mt-4 flex justify-end gap-2">
            <Dialog.Close asChild>
              <button type="button" className="btn">
                {t('actions.cancel')}
              </button>
            </Dialog.Close>
            <button type="button" className="btn btn-primary" disabled={blocked || busy} onClick={() => onConfirm(reason.trim())}>
              {verb}
            </button>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}

import { liquid } from '@codemirror/lang-liquid';
import CodeMirror, { EditorView } from '@uiw/react-codemirror';
import { useQuery } from '@tanstack/react-query';
import { Link, useNavigate } from '@tanstack/react-router';
import { useEffect, useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { Problem } from '@/api/types';
import { getTheme } from '@/app/preferences';
import { useMe } from '@/auth/queries';
import { can, P } from '@/auth/permissions';
import { ConfirmDialog } from '@/components/ConfirmDialog';
import { ProblemBanner } from '@/components/ProblemBanner';
import { diffLines } from '@/lib/diff';
import { utc } from '@/lib/time';
import { BUILTIN, prettyBody, templateVersionsQuery, useActivateTemplate, useCreateTemplateVersion, useRenderDraft, type WebhookTemplate } from '@/notifications/queries';
import { PreviewPane, SampleSourcePicker, StoredTemplatePreview } from '@/notifications/TemplatePreview';
import { BuiltinBadge } from '@/routes/Templates';

const FORMATS = ['json', 'text'] as const;
const STARTER = `{
  "event": "{{ event }}",
  "title": "[{{ episode.severity | upcase }}] {{ episode.summary }}",
  "url": "{{ episode.url }}"
}`;

interface Draft {
  name: string;
  description: string;
  format: string;
  contentType: string;
  body: string;
}

function draftFrom(v: WebhookTemplate | undefined, clone: boolean): Draft {
  return {
    name: v ? (clone ? `${v.name}-copy` : v.name) : '',
    description: v?.description ?? '',
    format: v?.format ?? 'json',
    contentType: v?.contentType ?? 'application/json',
    body: v?.body ?? STARTER,
  };
}

function editorTheme(): 'light' | 'dark' {
  const pref = getTheme();
  if (pref !== 'system') return pref;
  return typeof window !== 'undefined' && window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
}

/**
 * Template editor (08 §3.7b): `/templates/new` (optionally `?from=id` to clone a built-in) and `/templates/$id` with the
 * version rail, CodeMirror with Liquid highlighting, model reference, live render preview, inline validation, version diff
 * and activation. Built-ins are read-only — the only action is Clone.
 */
export function TemplateEditorPage({ id, from }: { id?: string; from?: string }) {
  const { t } = useTranslation();
  const me = useMe();
  const navigate = useNavigate();
  const canManage = can(me.data, P.destinationManage);
  const sourceId = id ?? from;
  const versions = useQuery({ ...templateVersionsQuery(sourceId ?? ''), enabled: !!sourceId });
  const sorted = useMemo(() => [...(versions.data ?? [])].sort((a, b) => b.version - a.version), [versions.data]);
  const active = sorted.find((v) => v.active);
  const [selected, setSelected] = useState<number | null>(null);
  const current = sorted.find((v) => v.version === (selected ?? active?.version ?? sorted[0]?.version));
  const readOnly = !canManage || (!!id && !!current?.builtin);
  const isNew = !id;

  const [draft, setDraft] = useState<Draft | null>(null);
  const effective = draft ?? draftFrom(current, isNew && !!from);
  const setField = <K extends keyof Draft>(key: K, value: Draft[K]) => setDraft({ ...effective, [key]: value });
  const dirty = draft !== null && (isNew || draft.body !== current?.body || draft.contentType !== current.contentType || draft.description !== (current.description ?? ''));

  const [sampleEpisode, setSampleEpisode] = useState('');
  const [compareWith, setCompareWith] = useState<number | ''>('');
  const [problem, setProblem] = useState<Problem | null>(null);
  const [confirmActivate, setConfirmActivate] = useState(false);
  const render = useRenderDraft();
  const create = useCreateTemplateVersion();
  const activate = useActivateTemplate();

  // Live preview: render the draft 400 ms after the last keystroke; stored versions are previewed from the server cache.
  useEffect(() => {
    if (!dirty) return;
    const handle = setTimeout(() => render.mutate({ format: effective.format, body: effective.body, contentType: effective.contentType || null, sampleEvent: null }), 400);
    return () => clearTimeout(handle);
    // eslint-disable-next-line react-hooks/exhaustive-deps -- `render` is a stable mutation handle
  }, [dirty, effective.body, effective.format, effective.contentType]);

  const bodyLabel = t('templates.form.body');
  const extensions = useMemo(() => [liquid(), EditorView.lineWrapping, EditorView.contentAttributes.of({ 'aria-label': bodyLabel, tabindex: '0' })], [bodyLabel]);

  const save = () => {
    setProblem(null);
    create.mutate(
      { name: effective.name.trim(), format: effective.format, body: effective.body, contentType: effective.contentType.trim() || null, description: effective.description.trim() || null, templateId: id ?? null, sampleEvent: null },
      {
        onSuccess: (v) => {
          setDraft(null);
          setSelected(v.version);
          if (isNew) void navigate({ to: '/templates/$id', params: { id: v.id } });
        },
        onError: (e) => setProblem(e as unknown as Problem),
      },
    );
  };

  if (sourceId && versions.isPending) return <p className="p-4 text-ink-2">{t('detail.loading')}</p>;
  if (sourceId && versions.isError) {
    return (
      <div className="p-4">
        <ProblemBanner problem={versions.error as unknown as Problem} />
      </div>
    );
  }
  const compare = compareWith === '' ? undefined : sorted.find((v) => v.version === compareWith);
  const previewProblem = render.isError ? (render.error as unknown as Problem) : null;
  const inlineErrors = (problem?.errors ?? previewProblem?.errors ?? []).filter((e) => e.path.startsWith('body') || e.path === '');

  return (
    <div className="flex h-full min-h-0 flex-col bg-surface">
      <header className="flex flex-wrap items-center gap-2 border-b border-line px-4 py-3">
        <Link to="/templates" className="text-xs text-ink-2">
          ← {t('templates.title')}
        </Link>
        <h1 className="m-0 text-md" data-testid="template-title">
          {isNew ? t('templates.new') : current?.name}
        </h1>
        {current?.builtin && !isNew && <BuiltinBadge />}
        {current && !isNew && (
          <span className={`badge ${current.active ? '' : 'border-dashed'}`} data-testid="version-state">
            v{current.version} · {current.active ? t('templates.active') : t('templates.inactive')}
          </span>
        )}
        <span className="ml-auto flex flex-wrap gap-1">
          {!isNew && current?.builtin && canManage && (
            <Link to="/templates/new" search={{ from: current.id }} className="btn hover:no-underline">
              {t('templates.clone')}
            </Link>
          )}
          {!isNew && current && !current.active && !current.builtin && canManage && (
            <button type="button" className="btn" onClick={() => setConfirmActivate(true)} disabled={activate.isPending}>
              {t('templates.activate')}
            </button>
          )}
          {!readOnly && (
            <button type="button" className="btn btn-primary" onClick={save} disabled={create.isPending || !effective.name.trim() || (!isNew && !dirty)}>
              {isNew ? t('templates.create') : t('templates.saveVersion')}
            </button>
          )}
        </span>
      </header>

      <div className="grid min-h-0 flex-1 grid-cols-[minmax(0,3fr)_minmax(0,2fr)] max-lg:grid-cols-1 max-lg:overflow-y-auto">
        <section className="flex min-h-0 flex-col gap-3 overflow-y-auto border-r border-line p-4 text-sm max-lg:border-r-0">
          {!isNew && sorted.length > 0 && (
            <div className="flex flex-wrap items-center gap-2 text-xs">
              <span className="text-ink-2">{t('templates.versions')}</span>
              <div role="tablist" aria-label={t('templates.versions')} className="flex flex-wrap gap-1">
                {sorted.map((v) => (
                  <button
                    key={v.version}
                    type="button"
                    role="tab"
                    aria-selected={v.version === current?.version}
                    className={`btn btn-sm ${v.version === current?.version ? 'bg-surface-2 font-semibold' : ''}`}
                    onClick={() => {
                      setSelected(v.version);
                      setDraft(null);
                      setCompareWith('');
                    }}
                    title={`${utc(v.createdAt)} · ${v.createdBy ?? ''}`}
                  >
                    v{v.version}
                    {v.active ? ' ●' : ''}
                  </button>
                ))}
              </div>
              {sorted.length > 1 && (
                <label className="ml-auto flex items-center gap-1 text-ink-2">
                  {t('templates.compareWith')}
                  <select className="input h-7 w-auto text-sm" value={compareWith} onChange={(e) => setCompareWith(e.target.value === '' ? '' : Number(e.target.value))}>
                    <option value="">—</option>
                    {sorted
                      .filter((v) => v.version !== current?.version)
                      .map((v) => (
                        <option key={v.version} value={v.version}>
                          v{v.version}
                        </option>
                      ))}
                  </select>
                </label>
              )}
            </div>
          )}

          <div className="grid gap-3 sm:grid-cols-[2fr_1fr_2fr]">
            <label className="block">
              <span className="text-ink-2">{t('templates.form.name')}</span>
              <input className="input mt-1" required value={effective.name} disabled={readOnly || !isNew} onChange={(e) => setField('name', e.target.value)} autoFocus={isNew} />
            </label>
            <label className="block">
              <span className="text-ink-2">{t('templates.form.format')}</span>
              <select
                className="input mt-1"
                value={effective.format}
                disabled={readOnly}
                onChange={(e) => {
                  const format = e.target.value;
                  setDraft({ ...effective, format, contentType: format === 'json' ? 'application/json' : 'text/plain; charset=utf-8' });
                }}
              >
                {FORMATS.map((f) => (
                  <option key={f} value={f}>
                    {f}
                  </option>
                ))}
              </select>
            </label>
            <label className="block">
              <span className="text-ink-2">{t('templates.form.contentType')}</span>
              <input className="input mono mt-1" value={effective.contentType} disabled={readOnly} onChange={(e) => setField('contentType', e.target.value)} />
            </label>
          </div>
          <label className="block">
            <span className="text-ink-2">{t('templates.form.description')}</span>
            <input className="input mt-1" value={effective.description} disabled={readOnly} onChange={(e) => setField('description', e.target.value)} />
          </label>

          {compare ? (
            <div className="rounded-md border border-line" aria-label={t('templates.diffLabel', { from: compare.version, to: current?.version ?? 0 })}>
              <div className="flex h-7 items-center border-b border-line px-2 text-xs text-ink-2">{t('templates.diffLabel', { from: compare.version, to: current?.version ?? 0 })}</div>
              <pre tabIndex={0} className="mono m-0 max-h-[480px] overflow-auto p-2 text-xs leading-snug">
                {diffLines(compare.body ?? '', effective.body).map((l, i) => (
                  <div key={i} className={l.op === 'add' ? 'bg-[color-mix(in_oklab,var(--sev-low)_18%,transparent)]' : l.op === 'del' ? 'bg-[color-mix(in_oklab,var(--sev-critical)_18%,transparent)] line-through' : ''}>
                    <span aria-hidden="true" className="inline-block w-4 text-ink-2">
                      {l.op === 'add' ? '+' : l.op === 'del' ? '−' : ' '}
                    </span>
                    {l.text}
                  </div>
                ))}
              </pre>
            </div>
          ) : (
            <div className="rounded-md border border-line" data-testid="template-editor">
              <div className="flex h-7 items-center justify-between border-b border-line px-2 text-xs text-ink-2">
                <span>{t('templates.form.body')}</span>
                <span>{readOnly ? t('templates.readOnly') : 'Liquid'}</span>
              </div>
              <CodeMirror value={effective.body} height="420px" theme={editorTheme()} extensions={extensions} readOnly={readOnly} basicSetup={{ lineNumbers: true, foldGutter: false, highlightActiveLine: !readOnly }} onChange={(value) => setField('body', value)} />
            </div>
          )}
          {inlineErrors.length > 0 && (
            <ul className="m-0 list-none p-0 text-xs" role="alert" style={{ color: 'var(--sev-critical)' }}>
              {inlineErrors.map((e) => (
                <li key={`${e.path}:${e.message}`} className="mono">
                  {e.path ? `${e.path}: ` : ''}
                  {e.message}
                </li>
              ))}
            </ul>
          )}
          {problem && !problem.errors?.length && (
            <div aria-live="polite">
              <ProblemBanner problem={problem} onDismiss={() => setProblem(null)} />
            </div>
          )}
        </section>

        <aside className="flex min-h-0 flex-col gap-3 overflow-y-auto p-4 text-sm">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <span className="text-ink-2">{dirty ? t('templates.previewDraft') : t('templates.preview')}</span>
            <SampleSourcePicker value={sampleEpisode} onChange={setSampleEpisode} />
          </div>
          {dirty || isNew ? (
            <PreviewPane body={render.data ? prettyBody(render.data.body, render.data.contentType) : null} contentType={render.data?.contentType} problem={previewProblem} loading={render.isPending} />
          ) : current ? (
            <StoredTemplatePreview templateId={current.id} version={current.version} episodeId={sampleEpisode || undefined} />
          ) : null}
          {isNew && sampleEpisode && <p className="m-0 text-xs text-ink-2">{t('templates.draftSampleOnly')}</p>}
          <details className="rounded-md border border-line" open>
            <summary className="cursor-pointer px-2 py-1 text-xs text-ink-2">{t('templates.modelReference')}</summary>
            <div className="p-2 text-xs text-ink-2">
              <p className="m-0 mb-2">{t('templates.modelHint')}</p>
              <StoredTemplatePreview templateId={BUILTIN.genericJson} version={1} episodeId={sampleEpisode || undefined} />
            </div>
          </details>
        </aside>
      </div>

      <ConfirmDialog
        open={confirmActivate}
        onOpenChange={setConfirmActivate}
        title={t('templates.activateTitle', { version: current?.version ?? 0 })}
        description={t('templates.activateWhy', { name: current?.name ?? '' })}
        verb={t('templates.activate')}
        busy={activate.isPending}
        onConfirm={() => {
          if (!current) return;
          activate.mutate({ id: current.id, version: current.version }, { onSuccess: () => setConfirmActivate(false), onError: (e) => setProblem(e as unknown as Problem) });
        }}
      />
    </div>
  );
}

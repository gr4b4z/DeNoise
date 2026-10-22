import { yaml } from '@codemirror/lang-yaml';
import CodeMirror, { EditorView } from '@uiw/react-codemirror';
import { useMemo } from 'react';
import { getTheme } from '@/app/preferences';

function editorTheme(): 'light' | 'dark' {
  const pref = getTheme();
  if (pref !== 'system') return pref;
  return typeof window !== 'undefined' && window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
}

/** 08 §4 `YamlEditor`: CodeMirror 6 with YAML highlighting; the content is labelled and focusable for keyboard users. */
export function YamlEditor({ value, onChange, readOnly, height = '360px', label, testId }: { value: string; onChange?: (value: string) => void; readOnly?: boolean; height?: string; label: string; testId?: string }) {
  const extensions = useMemo(() => [yaml(), EditorView.lineWrapping, EditorView.contentAttributes.of({ 'aria-label': label, tabindex: '0' })], [label]);
  return (
    <div className="rounded-md border border-line" data-testid={testId}>
      <CodeMirror value={value} height={height} theme={editorTheme()} extensions={extensions} readOnly={readOnly} basicSetup={{ lineNumbers: true, foldGutter: true, highlightActiveLine: !readOnly }} onChange={(v) => onChange?.(v)} />
    </div>
  );
}

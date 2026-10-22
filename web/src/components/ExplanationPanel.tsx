/** Renders the server-built explanation and the routing `why`; the UI never invents its own reasoning (08 §1). */
export function ExplanationPanel({ explanation, why }: { explanation: string; why?: string | null }) {
  return (
    <section aria-labelledby="explanation-heading" className="rounded-md border border-line bg-surface-2 px-3 py-2 text-sm">
      <h3 id="explanation-heading" className="text-xs text-ink-2">
        Why you see this
      </h3>
      <p className="m-0 mt-1 whitespace-pre-line">{explanation}</p>
      {why && <p className="m-0 mt-1 text-ink-2">Routing: {why}</p>}
    </section>
  );
}

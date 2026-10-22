import { useParams } from '@tanstack/react-router';
import type React from 'react';
import { isPolicyKind } from '@/policies/queries';
import { NotFoundPage } from '@/routes/Errors';
import { PoliciesPage } from '@/routes/Policies';
import { PolicyEditorPage } from '@/routes/PolicyEditor';

/**
 * Route components for `/policies/$kind` and `/policies/$kind/$id`, kept out of `router.tsx` with explicit return types
 * and loose params: both pages link to each other, and letting the route tree's type depend on them collapsed every
 * route's inferred params to `any`.
 */
export function PoliciesRoute(): React.JSX.Element {
  const { kind } = useParams({ strict: false });
  return isPolicyKind(kind) ? <PoliciesPage kind={kind} /> : <NotFoundPage />;
}

export function PolicyRoute(): React.JSX.Element {
  const { kind, id } = useParams({ strict: false });
  return isPolicyKind(kind) && id ? <PolicyEditorPage kind={kind} id={id} /> : <NotFoundPage />;
}

import { createRootRouteWithContext, createRoute, createRouter, Outlet, redirect } from '@tanstack/react-router';
import type { QueryClient } from '@tanstack/react-query';
import { QUEUE_VIEWS, type QueueView } from '@/api/types';
import { api } from '@/api/client';
import { meQuery, UnauthorizedError } from '@/auth/queries';
import { queryClient } from './queryClient';
import { Shell } from './Shell';
import { LoginPage } from '@/routes/Login';
import { ChangePasswordPage } from '@/routes/ChangePassword';
import { QueuePage } from '@/routes/Queue';
import type { QueueSearch } from '@/routes/queueSearch';
import { EpisodePage } from '@/routes/Episode';
import { ForbiddenPage, NotFoundPage } from '@/routes/Errors';
import { HeartbeatsPage, type HeartbeatsSearch } from '@/routes/Heartbeats';
import { HeartbeatFormPage } from '@/routes/HeartbeatForm';
import { HeartbeatDetailPage } from '@/routes/HeartbeatDetail';
import { DestinationsPage } from '@/routes/Destinations';
import { DestinationPage } from '@/routes/DestinationForm';
import { TemplatesPage } from '@/routes/Templates';
import { TemplateEditorPage } from '@/routes/TemplateEditor';
import { IntegrationsPage } from '@/routes/Integrations';
import { IntegrationWizardPage } from '@/routes/IntegrationWizard';
import { IntegrationDetailPage } from '@/routes/IntegrationDetail';

interface RouterContext {
  queryClient: QueryClient;
}

const rootRoute = createRootRouteWithContext<RouterContext>()({
  component: () => <Outlet />,
  notFoundComponent: NotFoundPage,
});

function str(v: unknown): string | undefined {
  return typeof v === 'string' && v.length > 0 ? v : undefined;
}

const loginRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/login',
  validateSearch: (s: Record<string, unknown>): { returnTo?: string } => ({ returnTo: str(s.returnTo) }),
  component: LoginPage,
});

const changePasswordRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/login/change-password',
  validateSearch: (s: Record<string, unknown>): { returnTo?: string } => ({ returnTo: str(s.returnTo) }),
  component: ChangePasswordPage,
});

/** Authenticated shell: loads /me once; 401 → login with the return URL; forced change → change-password (08 §7). */
const shellRoute = createRoute({
  getParentRoute: () => rootRoute,
  id: 'shell',
  beforeLoad: async ({ context, location }) => {
    try {
      const me = await context.queryClient.query({ ...meQuery(), staleTime: 'static' });
      if (me.mustChangePassword) throw redirect({ to: '/login/change-password', search: { returnTo: location.href } });
      return { me };
    } catch (error) {
      if (error instanceof UnauthorizedError) throw redirect({ to: '/login', search: { returnTo: location.href } });
      throw error;
    }
  },
  component: Shell,
});

const indexRoute = createRoute({
  getParentRoute: () => shellRoute,
  path: '/',
  beforeLoad: () => {
    throw redirect({ to: '/queue', search: { view: 'needsAttention' } });
  },
});

const queueRoute = createRoute({
  getParentRoute: () => shellRoute,
  path: '/queue',
  validateSearch: (s: Record<string, unknown>): QueueSearch => {
    const view = QUEUE_VIEWS.includes(s.view as QueueView) ? (s.view as QueueView) : 'needsAttention';
    const severity = Array.isArray(s.severity) ? s.severity.filter((x): x is string => typeof x === 'string') : typeof s.severity === 'string' ? [s.severity] : undefined;
    return { view, severity: severity?.length ? severity : undefined, team: str(s.team), environment: str(s.environment), service: str(s.service), q: str(s.q), episode: str(s.episode) };
  },
  component: QueuePage,
});

const historyRoute = createRoute({
  getParentRoute: () => shellRoute,
  path: '/history',
  beforeLoad: () => {
    throw redirect({ to: '/queue', search: { view: 'closed' } });
  },
});

const episodeRoute = createRoute({
  getParentRoute: () => shellRoute,
  path: '/episodes/$id',
  component: function EpisodeRoute() {
    const { id } = episodeRoute.useParams();
    return <EpisodePage id={id} />;
  },
});

const heartbeatsRoute = createRoute({
  getParentRoute: () => shellRoute,
  path: '/heartbeats',
  validateSearch: (s: Record<string, unknown>): HeartbeatsSearch => ({ state: str(s.state), team: str(s.team), q: str(s.q) }),
  component: HeartbeatsPage,
});

const heartbeatNewRoute = createRoute({ getParentRoute: () => shellRoute, path: '/heartbeats/new', component: () => <HeartbeatFormPage /> });

const heartbeatRoute = createRoute({
  getParentRoute: () => shellRoute,
  path: '/heartbeats/$id',
  component: function HeartbeatRoute() {
    const { id } = heartbeatRoute.useParams();
    return <HeartbeatDetailPage id={id} />;
  },
});

const heartbeatEditRoute = createRoute({
  getParentRoute: () => shellRoute,
  path: '/heartbeats/$id/edit',
  component: function HeartbeatEditRoute() {
    const { id } = heartbeatEditRoute.useParams();
    return <HeartbeatDetailPage id={id} edit />;
  },
});

const destinationsRoute = createRoute({ getParentRoute: () => shellRoute, path: '/destinations', component: DestinationsPage });
const destinationNewRoute = createRoute({ getParentRoute: () => shellRoute, path: '/destinations/new', component: () => <DestinationPage /> });
const destinationRoute = createRoute({
  getParentRoute: () => shellRoute,
  path: '/destinations/$id',
  component: function DestinationRoute() {
    const { id } = destinationRoute.useParams();
    return <DestinationPage id={id} />;
  },
});

const templatesRoute = createRoute({ getParentRoute: () => shellRoute, path: '/templates', component: TemplatesPage });
const templateNewRoute = createRoute({
  getParentRoute: () => shellRoute,
  path: '/templates/new',
  validateSearch: (s: Record<string, unknown>): { from?: string } => ({ from: str(s.from) }),
  component: function TemplateNewRoute() {
    const { from } = templateNewRoute.useSearch();
    return <TemplateEditorPage from={from} />;
  },
});
const templateRoute = createRoute({
  getParentRoute: () => shellRoute,
  path: '/templates/$id',
  component: function TemplateRoute() {
    const { id } = templateRoute.useParams();
    return <TemplateEditorPage id={id} />;
  },
});

const integrationsRoute = createRoute({ getParentRoute: () => shellRoute, path: '/integrations', component: IntegrationsPage });
const integrationNewRoute = createRoute({ getParentRoute: () => shellRoute, path: '/integrations/new', component: IntegrationWizardPage });
const integrationRoute = createRoute({
  getParentRoute: () => shellRoute,
  path: '/integrations/$id',
  component: function IntegrationRoute() {
    const { id } = integrationRoute.useParams();
    return <IntegrationDetailPage id={id} />;
  },
});

const forbiddenRoute = createRoute({ getParentRoute: () => rootRoute, path: '/403', component: ForbiddenPage });
const notFoundRoute = createRoute({ getParentRoute: () => rootRoute, path: '/404', component: NotFoundPage });

const logoutRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/logout',
  beforeLoad: async () => {
    await api.POST('/auth/logout');
    queryClient.clear();
    throw redirect({ to: '/login', search: { returnTo: undefined } });
  },
});

const routeTree = rootRoute.addChildren([loginRoute, changePasswordRoute, forbiddenRoute, notFoundRoute, logoutRoute, shellRoute.addChildren([indexRoute, queueRoute, historyRoute, episodeRoute, heartbeatsRoute, heartbeatNewRoute, heartbeatRoute, heartbeatEditRoute, destinationsRoute, destinationNewRoute, destinationRoute, templatesRoute, templateNewRoute, templateRoute, integrationsRoute, integrationNewRoute, integrationRoute])]);

export const router = createRouter({ routeTree, context: { queryClient }, defaultPreload: 'intent', scrollRestoration: true });

declare module '@tanstack/react-router' {
  interface Register {
    router: typeof router;
  }
}

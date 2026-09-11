import { QueryClient } from '@tanstack/react-query';

export const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 15_000,
      refetchOnWindowFocus: true,
      retry: (failureCount, error) => {
        if (error instanceof Error && error.name === 'UnauthorizedError') return false;
        return failureCount < 1;
      },
    },
  },
});

import js from '@eslint/js';
import globals from 'globals';
import tseslint from 'typescript-eslint';
import reactHooks from 'eslint-plugin-react-hooks';
import reactRefresh from 'eslint-plugin-react-refresh';

export default tseslint.config(
  { ignores: ['dist', 'coverage', 'playwright-report', 'test-results', 'src/api/schema.d.ts'] },
  {
    files: ['**/*.{ts,tsx}'],
    extends: [js.configs.recommended, ...tseslint.configs.strictTypeChecked, ...tseslint.configs.stylisticTypeChecked],
    languageOptions: {
      ecmaVersion: 2022,
      globals: { ...globals.browser, ...globals.node },
      parserOptions: { projectService: true, tsconfigRootDir: import.meta.dirname },
    },
    plugins: { 'react-hooks': reactHooks, 'react-refresh': reactRefresh },
    rules: {
      ...reactHooks.configs.recommended.rules,
      'react-refresh/only-export-components': ['warn', { allowConstantExport: true }],
      '@typescript-eslint/no-unnecessary-condition': 'off',
      // TanStack Router redirects and RFC 9457 problem objects are thrown by design.
      '@typescript-eslint/only-throw-error': 'off',
      '@typescript-eslint/prefer-nullish-coalescing': ['error', { ignorePrimitives: { string: true, boolean: true } }],
      '@typescript-eslint/restrict-template-expressions': ['error', { allowNumber: true, allowBoolean: true }],
      '@typescript-eslint/no-confusing-void-expression': 'off',
      '@typescript-eslint/no-misused-promises': ['error', { checksVoidReturn: { attributes: false } }],
      // AGENTS.md rule 12: the UI talks to the API only through the generated openapi-fetch client (src/api/client.ts).
      'no-restricted-globals': ['error', { name: 'fetch', message: 'Use the generated client in src/api/client.ts (AGENTS.md rule 12).' }],
      'no-restricted-properties': [
        'error',
        { object: 'window', property: 'fetch', message: 'Use the generated client in src/api/client.ts (AGENTS.md rule 12).' },
        { object: 'globalThis', property: 'fetch', message: 'Use the generated client in src/api/client.ts (AGENTS.md rule 12).' },
      ],
    },
  },
  {
    // The client itself, tests and the SSE transport are the sanctioned places for a raw transport.
    files: ['src/api/client.ts', 'src/test/**', 'src/**/*.test.{ts,tsx}', 'e2e/**'],
    rules: { 'no-restricted-globals': 'off', 'no-restricted-properties': 'off', '@typescript-eslint/no-non-null-assertion': 'off' },
  },
);

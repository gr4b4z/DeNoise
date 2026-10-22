export type Theme = 'system' | 'light' | 'dark';
export type Density = 'compact' | 'comfortable';

const THEME_KEY = 'denoise.theme';
const DENSITY_KEY = 'denoise.density';

function read(key: string): string | null {
  try {
    return localStorage.getItem(key);
  } catch {
    return null;
  }
}

function write(key: string, value: string | null): void {
  try {
    if (value === null) localStorage.removeItem(key);
    else localStorage.setItem(key, value);
  } catch {
    // storage unavailable: preference lasts for the tab only
  }
}

export function getTheme(): Theme {
  const v = read(THEME_KEY);
  return v === 'light' || v === 'dark' ? v : 'system';
}

export function setTheme(theme: Theme): void {
  write(THEME_KEY, theme === 'system' ? null : theme);
  applyTheme(theme);
}

function applyTheme(theme: Theme): void {
  const root = document.documentElement;
  if (theme === 'system') root.removeAttribute('data-theme');
  else root.setAttribute('data-theme', theme);
}

export function getDensity(): Density {
  return read(DENSITY_KEY) === 'comfortable' ? 'comfortable' : 'compact';
}

export function setDensity(density: Density): void {
  write(DENSITY_KEY, density === 'compact' ? null : density);
  applyDensity(density);
}

function applyDensity(density: Density): void {
  const root = document.documentElement;
  if (density === 'compact') root.removeAttribute('data-density');
  else root.setAttribute('data-density', density);
}

/** Theme follows the OS unless the user chose one; both preferences persist per browser (08 §0). */
export function applyStoredPreferences(): void {
  applyTheme(getTheme());
  applyDensity(getDensity());
}

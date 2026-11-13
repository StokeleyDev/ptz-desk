// Single light scheme (Braun-style hardware). The dark scheme was retired; the hook stays so callers need no changes.
export function useScheme() {
  if (typeof document !== 'undefined') { document.documentElement.dataset.scheme = 'light'; document.documentElement.style.colorScheme = 'light'; }
  return ['light', () => {}];
}
export function SchemeToggle() { return null; }

const raw = Number(import.meta.env.VITE_DASHBOARD_REFRESH_MS ?? 30000);

/** Polling interval for live dashboards; TanStack Query pauses it while the tab is hidden. */
export const DASHBOARD_REFRESH_MS: number | false = Number.isFinite(raw) && raw > 0 ? Math.max(raw, 5000) : false;

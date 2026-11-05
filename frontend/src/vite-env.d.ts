/// <reference types="vite/client" />

interface ImportMetaEnv {
  readonly VITE_API_BASE_URL?: string;
  readonly VITE_DASHBOARD_REFRESH_MS?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}

/// <reference types="vitest/config" />
import tailwindcss from "@tailwindcss/vite";
import react from "@vitejs/plugin-react";
import { fileURLToPath, URL } from "node:url";
import { defineConfig, loadEnv } from "vite";

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), "");
  // In development the SPA calls /api on its own origin and Vite proxies to FastAPI, so the
  // session cookie stays first-party (SameSite=Lax works without cross-site cookies).
  const backend = env.VITE_DEV_BACKEND_URL || "http://127.0.0.1:8000";
  return {
    plugins: [react(), tailwindcss()],
    resolve: { alias: { "@": fileURLToPath(new URL("./src", import.meta.url)) } },
    server: {
      port: Number(env.VITE_PORT || 5173),
      strictPort: true,
      proxy: { "/api": { target: backend, changeOrigin: false } },
    },
    test: {
      environment: "jsdom",
      globals: true,
      setupFiles: ["./src/test/setup.ts"],
      css: false,
    },
  };
});

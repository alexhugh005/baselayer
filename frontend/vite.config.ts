import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    strictPort: true,
    watch:
      process.env.DEV_POLLING === "true"
        ? { usePolling: true, interval: 500 }
        : undefined,
    proxy: {
      "/api": process.env.API_PROXY_TARGET ?? "http://127.0.0.1:5080",
      "/health": process.env.API_PROXY_TARGET ?? "http://127.0.0.1:5080",
    },
  },
});

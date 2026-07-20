import { defineConfig } from "vite";
import { resolve } from "path";

// Two build targets:
//   vite / vite build                → dev harness (index.html chat + HUD)
//   vite build --mode extension     → Chrome extension (dist-ext/, load unpacked)
export default defineConfig(({ mode }) => {
  if (mode === "extension") {
    return {
      publicDir: "extension/public", // manifest.json copied to dist-ext root
      build: {
        outDir: "dist-ext",
        emptyOutDir: true,
        rollupOptions: {
          input: {
            sidepanel: resolve(__dirname, "extension/sidepanel.html"),
            background: resolve(__dirname, "extension/background.ts"),
          },
          output: {
            entryFileNames: "[name].js",
            chunkFileNames: "chunks/[name]-[hash].js",
            assetFileNames: "assets/[name]-[hash][extname]",
          },
        },
      },
    };
  }
  return {};
});

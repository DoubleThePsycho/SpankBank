import { defineConfig } from "vite";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL(".", import.meta.url));

// Output goes to ../wwwroot/dist with stable file names; ASP.NET adds cache-busting (asp-append-version).
export default defineConfig({
  root,
  base: "/dist/",
  resolve: {
    alias: [
      { find: /^~\//, replacement: `${root}/` },
      { find: /^@\//, replacement: `${root}src/js/` },
    ],
  },
  css: {
    preprocessorOptions: {
      scss: { silenceDeprecations: ["import", "global-builtin", "mixed-decls", "color-functions", "slash-div"] },
    },
  },
  build: {
    outDir: "../wwwroot/dist",
    emptyOutDir: true,
    sourcemap: true,
    rollupOptions: {
      input: {
        application: `${root}src/application.ts`,
        posts: `${root}src/posts.ts`,
        watches: `${root}src/watches.ts`,
      },
      output: {
        entryFileNames: "[name].js",
        chunkFileNames: "chunks/[name].js",
        assetFileNames: (asset) => (asset.names?.[0]?.endsWith(".css") ? "[name][extname]" : "assets/[name]-[hash][extname]"),
      },
    },
  },
});

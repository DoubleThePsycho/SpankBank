import { defineConfig } from "vite";
import { fileURLToPath } from "node:url";
import { cpSync, readdirSync } from "node:fs";
import { join } from "node:path";

const root = fileURLToPath(new URL(".", import.meta.url));

// Ruffle (Flash emulator, MIT/Apache-2.0) is self-hosted: its script and wasm files are copied next to the bundles, and
// flash.ts loads them on posts that are .swf files.
const ruffleDir = join(root, "node_modules/@ruffle-rs/ruffle");
const copyRuffle = {
  name: "copy-ruffle",
  closeBundle() {
    const target = join(root, "../wwwroot/dist/ruffle");
    for (const file of readdirSync(ruffleDir))
      if (/\.(js|wasm)$/.test(file) || file.startsWith("LICENSE")) cpSync(join(ruffleDir, file), join(target, file));
  },
};

// Output goes to ../wwwroot/dist with stable file names; ASP.NET adds cache-busting (asp-append-version).
export default defineConfig({
  root,
  base: "/dist/",
  plugins: [copyRuffle],
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
        status: `${root}src/status.ts`,
        flash: `${root}src/flash.ts`,
      },
      output: {
        entryFileNames: "[name].js",
        chunkFileNames: "chunks/[name].js",
        assetFileNames: (asset) => (asset.names?.[0]?.endsWith(".css") ? "[name][extname]" : "assets/[name]-[hash][extname]"),
      },
    },
  },
});

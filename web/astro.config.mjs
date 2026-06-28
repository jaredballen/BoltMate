// @ts-check
import { defineConfig } from "astro/config";

// Static site only — Azure Static Web Apps Free serves from a folder of
// pre-rendered HTML. No SSR runtime. Stripe price + any other dynamic
// data is fetched at build time.
export default defineConfig({
  site: "https://boltmate.app",
  trailingSlash: "ignore",
  build: {
    format: "directory",
    inlineStylesheets: "auto",
  },
  compressHTML: true,
});

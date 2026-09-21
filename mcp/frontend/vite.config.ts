import { defineConfig } from 'vite'
import solidPlugin from 'vite-plugin-solid'
import tailwindVite from '@tailwindcss/vite'

export default defineConfig({
  plugins: [tailwindVite(), solidPlugin()],
  build: {
    target: 'esnext',
    outDir: '../WorkState.Api/wwwroot',
    emptyOutDir: true,
  },
  server: {
    // `pnpm dev` serves the SPA; the API serves the data. Proxying /api keeps the session cookie
    // same-origin here too, so dev and the built-into-wwwroot app have one auth story, not two.
    proxy: { '/api': 'http://localhost:5290' },
  },
})

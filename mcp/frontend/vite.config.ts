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
    // 127.0.0.1, not localhost: the API binds IPv4 loopback only, and localhost may resolve to ::1.
    proxy: { '/api': 'http://127.0.0.1:5290' },
  },
})

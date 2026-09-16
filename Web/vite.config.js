import { defineConfig } from 'vite';
export default defineConfig({
  base: '/hyper/',
  server: { port: 5178, strictPort: true, proxy: { '/hyper/api': 'http://127.0.0.1:18740' } },
  build: { target: 'es2022', chunkSizeWarningLimit: 1000 }
});

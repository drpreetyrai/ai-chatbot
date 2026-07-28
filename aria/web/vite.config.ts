import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    port: 5173,
    // The API base URL also comes from VITE_API_BASE_URL; the proxy keeps the dev
    // origin identical so cookie and CORS behaviour matches production more closely.
    proxy: { '/v1': { target: 'http://localhost:5199', changeOrigin: true } },
  },
})

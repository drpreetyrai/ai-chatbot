import { defineConfig } from 'vitest/config'
import react from '@vitejs/plugin-react'

export default defineConfig({
  plugins: [react()],
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test/setup.ts'],
    css: false,
    // Playwright owns e2e/. Without this vitest tries to run the browser specs in
    // jsdom, where they fail for reasons that have nothing to do with the product.
    exclude: ['e2e/**', 'node_modules/**', 'dist/**'],
  },
})

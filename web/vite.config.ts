import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    // Pinned on purpose. Without strictPort Vite quietly moves to the next free
    // port when 5173 is taken — usually a dev server left running — and the API's
    // allowlist no longer matches, which surfaces as an unexplained CORS failure.
    // Failing here instead points at the real problem: something else is running.
    port: 5173,
    strictPort: true,
  },
})

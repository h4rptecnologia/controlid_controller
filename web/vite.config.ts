import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// O browser so fala com a API da plataforma. Dispositivos Control iD nunca sao alcancados daqui.
export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      '/api': 'http://127.0.0.1:8080',
      '/health': 'http://127.0.0.1:8080',
    },
  },
})

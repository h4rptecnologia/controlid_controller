import { defineConfig, devices } from '@playwright/test'

// Contra a stack real do compose, sem nada simulado. Exige: docker compose up -d --build --wait web
export default defineConfig({
  testDir: './e2e-stack',
  reporter: 'list',
  use: { baseURL: process.env.STACK_URL ?? 'http://127.0.0.1:8081' },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
})

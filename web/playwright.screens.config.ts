import base from './playwright.config'

// So para revisao visual: fora da suite normal.
export default { ...base, testMatch: process.env['SCREEN_MATCH'] ?? '**/*.screens.ts' }

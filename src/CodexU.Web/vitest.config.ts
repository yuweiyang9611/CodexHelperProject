import { fileURLToPath } from 'node:url'
import { defineConfig } from 'vitest/config'
import vue from '@vitejs/plugin-vue'

// Unit tests for the presentation layer. Playwright owns tests/e2e; this config
// deliberately excludes that directory so the two runners never collide.
export default defineConfig({
  plugins: [vue()],
  define: {
    // host.ts reads this build-time constant at module scope.
    __APP_VERSION__: JSON.stringify('test'),
  },
  test: {
    environment: 'happy-dom',
    include: ['tests/unit/**/*.spec.ts'],
    // Pinned so assertions over local-time maths (month-to-date projection) do
    // not shift with the machine's zone. Matches playwright.config.ts.
    env: { TZ: 'Asia/Tokyo' },
    root: fileURLToPath(new URL('.', import.meta.url)),
    restoreMocks: true,
    unstubEnvs: true,
    unstubGlobals: true,
    coverage: {
      provider: 'v8',
      include: [
        'src/format.ts',
        'src/host.ts',
        'src/stores/**/*.ts',
        'src/composables/**/*.ts',
      ],
      reporter: ['text', 'lcov'],
    },
  },
})

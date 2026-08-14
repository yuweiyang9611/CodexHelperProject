import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

// https://vite.dev/config/
export default defineConfig({
  plugins: [vue()],
  define: {
    __APP_VERSION__: JSON.stringify(process.env.CODEXU_VERSION ?? 'development'),
  },
  base: '/',
  build: {
    target: 'es2022',
    sourcemap: false,
    assetsDir: 'assets',
  },
})

import { defineConfig } from 'vite';

export default defineConfig({
  server: {
    port: 5173,
    // Запросы к /api уходят на бэкенд, поэтому в коде фронта нет
    // ни хоста, ни порта — тот же путь работает и в разработке,
    // и когда статика раздаётся рядом с API.
    proxy: {
      '/api': {
        target: process.env.API_URL ?? 'http://127.0.0.1:5199',
        changeOrigin: true,
      },
    },
  },
});

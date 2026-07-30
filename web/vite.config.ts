import { defineConfig } from 'vite';

// Сайт живёт по адресу https://mrwd.github.io/science-timeline/, поэтому
// пути к ресурсам должны быть относительны этого префикса, а не корня домена.
// Переопределяется переменной BASE_PATH — например, для локального preview.
export default defineConfig({
  base: process.env.BASE_PATH ?? '/science-timeline/',
  server: {
    port: 5173,
  },
  build: {
    // Данные лежат в public/data и копируются как есть: их собирает
    // экспорт из базы, а не сборщик.
    assetsInlineLimit: 0,
  },
});

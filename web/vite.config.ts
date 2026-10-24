/// <reference types="vitest/config" />
import { configDefaults, defineConfig } from "vitest/config";
import react from "@vitejs/plugin-react";

export default defineConfig({
  plugins: [react()],
  test: {
    environment: "jsdom",
    globals: true,
    setupFiles: ["./src/test/setup.ts"],
    // Playwright specs live in e2e/ and are run by `npm run test:e2e`, not Vitest.
    exclude: [...configDefaults.exclude, "e2e/**", "test-results/**", "playwright-report/**"],
  },
});

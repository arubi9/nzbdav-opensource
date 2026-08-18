import { defineConfig } from "vitest/config";
import tsconfigPaths from "vite-tsconfig-paths";

export default defineConfig({
  test: {
    globals: true,
    environment: "node",
    setupFiles: ["./vitest.setup.ts"],
    css: {
      modules: { classNameStrategy: "non-scoped" },
    },
    include: ["app/**/*.test.ts", "app/**/*.test.tsx", "app/**/*.spec.ts", "app/**/*.spec.tsx"],
    // Integration tests start real servers and share process-level configuration.
    fileParallelism: false,
    maxWorkers: 1,
    minWorkers: 1,
  },
  plugins: [tsconfigPaths()],
});

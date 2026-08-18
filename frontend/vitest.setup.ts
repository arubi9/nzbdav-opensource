process.env.SESSION_KEY = process.env.SESSION_KEY || "test-session-key-fixture-not-a-secret";
// Tests must configure the backend websocket credential explicitly; production
// code never substitutes a fallback key for a missing deployment secret.
process.env.FRONTEND_BACKEND_API_KEY = process.env.FRONTEND_BACKEND_API_KEY || "test-backend-key-fixture-not-a-secret";
process.env.SETUP_SESSION_KEY = process.env.SETUP_SESSION_KEY || "test-setup-session-key";
process.env.CSRF_SESSION_KEY = process.env.CSRF_SESSION_KEY || "test-csrf-session-key";
process.env.NODE_ENV = "test";
(globalThis as Record<string, unknown>).IS_REACT_ACT_ENVIRONMENT = true;

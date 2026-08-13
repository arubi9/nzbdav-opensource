# -------- Stage 1: Build frontend --------
# Keep the multi-arch index digest (rather than an arch-specific manifest) so
# buildx can select linux/amd64 or linux/arm64 while retaining provenance.
FROM --platform=$BUILDPLATFORM node:22.15.0-alpine3.21@sha256:ad1aedbcc1b0575074a91ac146d6956476c1f9985994810e4ee02efd932a68fd AS frontend-build

WORKDIR /frontend
COPY ./frontend ./

RUN npm ci
RUN npm run build
RUN npm run build:server
# Test processes use deliberately explicit fixture credentials. They are not
# copied into the runtime stage and are never production fallbacks.
ENV NODE_ENV=test
ENV SESSION_KEY=frontend-test-session-key-fixture-not-a-secret-0123456789
ENV FRONTEND_BACKEND_API_KEY=frontend-test-backend-key-fixture-not-a-secret-0123456789
RUN npm test -- --run
RUN npm run test:server-startup
ENV FRONTEND_IMAGE_SMOKE_IN_BUILD=1
RUN npm run test:server-image-startup
RUN npm prune --omit=dev
RUN npm audit --omit=dev

# Keep the combined runtime on the same pinned Node release as the frontend.
# This target-platform stage avoids copying a build-platform binary on
# multi-architecture builds.
FROM --platform=$TARGETPLATFORM node:22.15.0-alpine3.21@sha256:ad1aedbcc1b0575074a91ac146d6956476c1f9985994810e4ee02efd932a68fd AS node-runtime

# -------- Stage 2: Build backend --------
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.302-alpine3.23@sha256:d8ee39817ca03a3757288e83c37ed73cc969a286c603b827c7cbe33add1c2d1c AS backend-build

WORKDIR /backend
COPY ./backend ./

# The project and its committed lock file declare both musl runtime graphs.
# Docker's TARGETARCH is amd64/arm64 while .NET's musl RID uses x64/arm64.
ARG TARGETARCH
RUN dotnet restore --locked-mode \
    && case "$TARGETARCH" in \
        amd64) RID=linux-musl-x64 ;; \
        arm64) RID=linux-musl-arm64 ;; \
        *) echo "Unsupported TARGETARCH: $TARGETARCH" >&2; exit 1 ;; \
    esac \
    && dotnet publish -c Release -r "$RID" -o ./publish --no-restore

# -------- Stage 3: Combined runtime image --------
FROM mcr.microsoft.com/dotnet/aspnet:10.0.10-alpine3.23@sha256:27b6b84beeede74fd16886177d360799c8e4299ceadfbd64eef57bafead7878a

WORKDIR /app

# Prepare environment
RUN mkdir /config \
    && apk add --no-cache \
        gcompat=1.1.0-r4 \
        shadow=4.18.0-r0 \
        su-exec=0.3-r0 \
        bash=5.3.3-r1 \
        curl=8.20.0-r0 \
        krb5-libs=1.22.1-r0 \
        ffmpeg=8.0.1-r1

# Copy the pinned Node 22.15 runtime used by the frontend.
COPY --from=node-runtime /usr/local/bin/node /usr/local/bin/node
COPY --from=node-runtime /usr/local/bin/npm /usr/local/bin/npm
COPY --from=node-runtime /usr/local/bin/npx /usr/local/bin/npx
COPY --from=node-runtime /usr/local/lib/node_modules/npm /usr/local/lib/node_modules/npm

# Copy frontend
COPY --from=frontend-build /frontend/node_modules ./frontend/node_modules
COPY --from=frontend-build /frontend/package.json ./frontend/package.json
COPY --from=frontend-build /frontend/dist-node ./frontend/dist-node
COPY --from=frontend-build /frontend/build ./frontend/build

# Copy backend
COPY --from=backend-build /backend/publish ./backend

# Entry and runtime setup.  bootstrap-secrets.sh is sourced by the entrypoint
# at runtime and must be packaged in the final image (not only the build
# context).
COPY entrypoint.sh /entrypoint.sh
COPY bootstrap-secrets.sh /bootstrap-secrets.sh
# Docker executes this file directly. Reject CRLF before a cached runtime
# layer can turn /bin/sh\r into an opaque "no such file" startup failure.
RUN test "$(head -c 10 /entrypoint.sh | od -An -tx1 | tr -d '[:space:]')" = "23212f62696e2f73680a" \
    && ! grep -q "$(printf '\r')" /entrypoint.sh \
    && chmod +x /entrypoint.sh /bootstrap-secrets.sh

EXPOSE 3000
ARG NZBDAV_VERSION
ENV NZBDAV_VERSION=${NZBDAV_VERSION}
ENV NODE_ENV=production
ENV LOG_LEVEL=warning

CMD ["/entrypoint.sh"]

# Build the project
FROM node:22.15.0-alpine3.21@sha256:ad1aedbcc1b0575074a91ac146d6956476c1f9985994810e4ee02efd932a68fd AS build-env
WORKDIR /nzb-webdav
COPY ./ ./
RUN npm ci
RUN npm run build
RUN npm run build:server
# Explicit non-secret fixtures are used only by test processes and stay in
# this discarded stage; production startup requires injected credentials.
ENV NODE_ENV=test
ENV SESSION_KEY=frontend-test-session-key-fixture-not-a-secret-0123456789
ENV FRONTEND_BACKEND_API_KEY=frontend-test-backend-key-fixture-not-a-secret-0123456789
RUN npm test -- --run
RUN npm run test:server-startup
ENV FRONTEND_IMAGE_SMOKE_IN_BUILD=1
RUN npm run test:server-image-startup
RUN npm prune --omit=dev
RUN npm audit --omit=dev

# Build the production image
FROM node:22.15.0-alpine3.21@sha256:ad1aedbcc1b0575074a91ac146d6956476c1f9985994810e4ee02efd932a68fd
EXPOSE 3000
WORKDIR /nzb-webdav
ENV NODE_ENV=production
COPY --from=build-env --chown=node:node /nzb-webdav/node_modules ./node_modules
COPY --from=build-env --chown=node:node /nzb-webdav/package.json ./package.json
COPY --from=build-env --chown=node:node /nzb-webdav/package-lock.json ./package-lock.json
COPY --from=build-env --chown=node:node /nzb-webdav/dist-node ./dist-node
COPY --from=build-env --chown=node:node /nzb-webdav/build ./build
COPY --chown=node:node entrypoint.sh /usr/local/bin/nzbdav-frontend-entrypoint
RUN chmod 755 /usr/local/bin/nzbdav-frontend-entrypoint
USER node
ENTRYPOINT ["/usr/local/bin/nzbdav-frontend-entrypoint"]

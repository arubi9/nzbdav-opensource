# Build the project. These are multi-architecture manifest digests verified from
# the official MCR tags; the runtime image carries .NET 10.0.10.
FROM mcr.microsoft.com/dotnet/sdk:10.0.302-alpine3.23@sha256:d8ee39817ca03a3757288e83c37ed73cc969a286c603b827c7cbe33add1c2d1c AS build-env
WORKDIR /nzb-webdav
COPY ./ ./
ARG TARGETARCH
RUN case "$TARGETARCH" in \
        amd64) RID=linux-musl-x64 ;; \
        arm64) RID=linux-musl-arm64 ;; \
        *) echo "Unsupported TARGETARCH: $TARGETARCH" >&2; exit 1 ;; \
    esac \
    && dotnet restore NzbWebDAV.csproj --locked-mode \
    && dotnet publish NzbWebDAV.csproj -c Release -r "$RID" -o ./publish --no-restore

# Build the production image
FROM mcr.microsoft.com/dotnet/aspnet:10.0.10-alpine3.23@sha256:27b6b84beeede74fd16886177d360799c8e4299ceadfbd64eef57bafead7878a
EXPOSE 8080
WORKDIR /nzb-webdav
RUN mkdir /config
RUN apk add --no-cache libc6-compat shadow su-exec && rm -rf /var/cache/apk/*
COPY --from=build-env /nzb-webdav/publish ./
COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh
CMD ["/entrypoint.sh"]
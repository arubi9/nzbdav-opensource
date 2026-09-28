# Jellyfin's 12.0 image declares /config as a volume and starts
# /jellyfin/jellyfin directly. The plugin payload contains exactly its DLL and
# meta.json; the image also carries a checked source manifest outside the
# writable config volume. Startup never trusts an existing persisted plugin.
FROM mcr.microsoft.com/dotnet/sdk:10.0.401-alpine3.23@sha256:84c5d09dfeb4275f6540084e69331f140223eb968b5ef48023923b0b2eda156b AS plugin-build
WORKDIR /src
COPY jellyfin-plugin/Jellyfin.Plugin.Nzbdav/ Jellyfin.Plugin.Nzbdav/
RUN dotnet restore Jellyfin.Plugin.Nzbdav/Jellyfin.Plugin.Nzbdav.csproj --locked-mode \
    && dotnet publish Jellyfin.Plugin.Nzbdav/Jellyfin.Plugin.Nzbdav.csproj \
      --configuration Release \
      --output /tmp/nzbdav-publish \
      --no-self-contained --no-restore \
    && mkdir -p /opt/nzbdav-plugin \
    && cp /tmp/nzbdav-publish/Jellyfin.Plugin.Nzbdav.dll /opt/nzbdav-plugin/ \
    && cat > /opt/nzbdav-plugin/meta.json <<'EOF'
{
  "category": "General",
  "changelog": "",
  "description": "Stream media directly from NZBDAV without rclone.",
  "guid": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "name": "NZBDAV",
  "overview": "Integrates Jellyfin with NZBDAV for direct NNTP streaming.",
  "owner": "nzbdav",
  "targetAbi": "12.0.0.0",
  "timestamp": "0001-01-01T00:00:00.0000000Z",
  "version": "1.2.0.0",
  "status": "Active",
  "autoUpdate": true,
  "assemblies": []
}
EOF
# Jellyfin 12.0 writes this metadata in the same canonical form during
# discovery: the JSON has no terminal newline. Hash only that exact payload.
RUN size=$(stat -c %s /opt/nzbdav-plugin/meta.json); test "$size" -gt 0; \
    truncate -s "$((size - 1))" /opt/nzbdav-plugin/meta.json
# This manifest intentionally hashes exactly the two installable files.
RUN set -eu; test "$(find /opt/nzbdav-plugin -mindepth 1 -maxdepth 1 -type f | wc -l)" -eq 2; \
    test -f /opt/nzbdav-plugin/Jellyfin.Plugin.Nzbdav.dll; \
    test -f /opt/nzbdav-plugin/meta.json; \
    (cd /opt/nzbdav-plugin && sha256sum Jellyfin.Plugin.Nzbdav.dll meta.json | sort) \
      > /opt/nzbdav-plugin.manifest.sha256

FROM jellyfin/jellyfin:12.0@sha256:baba630419915985442f315f08b0cf46d9f4c8a0cc4bd38e94a6d35751dd5ef5
COPY --from=plugin-build /opt/nzbdav-plugin/ /opt/nzbdav-plugin/
COPY --from=plugin-build /opt/nzbdav-plugin.manifest.sha256 /opt/nzbdav-plugin.manifest.sha256
COPY jellyfin-stack/entrypoint.sh /usr/local/bin/nzbdav-jellyfin-entrypoint
RUN chmod 0755 /usr/local/bin/nzbdav-jellyfin-entrypoint
ENTRYPOINT ["/usr/local/bin/nzbdav-jellyfin-entrypoint"]

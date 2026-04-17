#!/bin/bash
set -e

PLUGIN_DIR="Jellyfin.Plugin.Nzbdav"
OUTPUT_DIR="artifacts/Nzbdav"
PLUGIN_GUID="a1b2c3d4-e5f6-7890-abcd-ef1234567890"
PLUGIN_VERSION="1.1.0.0"

echo "Building NZBDAV Jellyfin plugin..."
cd "$(dirname "$0")"

dotnet publish "$PLUGIN_DIR/$PLUGIN_DIR.csproj" \
    -c Release \
    -o "$OUTPUT_DIR" \
    --no-self-contained

# Create meta.json for Jellyfin plugin loader
cat > "$OUTPUT_DIR/meta.json" <<METAJSON
{
    "guid": "$PLUGIN_GUID",
    "name": "NZBDAV",
    "description": "Stream media directly from NZBDAV without rclone.",
    "overview": "Integrates Jellyfin with NZBDAV for direct NNTP streaming.",
    "owner": "nzbdav",
    "category": "General",
    "version": "$PLUGIN_VERSION",
    "targetAbi": "10.11.0.0",
    "status": "Active"
}
METAJSON

echo ""
echo "Plugin built successfully at: $OUTPUT_DIR/"
echo ""
echo "To install:"
echo "  1. Copy the $OUTPUT_DIR/ folder to your Jellyfin plugins directory:"
echo "     cp -r $OUTPUT_DIR/ /path/to/jellyfin/config/plugins/Nzbdav/"
echo ""
echo "  2. Restart Jellyfin"
echo ""
echo "  3. Go to Dashboard → Plugins → NZBDAV → Configure:"
echo "     - NZBDAV Server URL: https://your-nzbdav-server.com"
echo "     - API Key: (your NZBDAV API key)"
echo ""
echo "  4. Run the 'NZBDAV Library Sync' scheduled task to populate your library"

namespace backend.Tests.Deployment;

public sealed class EntrypointScriptTests
{
    [Fact]
    public void Entrypoint_UsesFixedPathsAndRotatesBeforePromotion()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var script = File.ReadAllText(Path.Combine(repoRoot, "entrypoint.sh"));

        Assert.Contains("BOOTSTRAP_SCRIPT=/bootstrap-secrets.sh", script);
        Assert.Contains("BACKEND_DIR=/app/backend", script);
        Assert.Contains("FRONTEND_DIR=/app/frontend", script);
        Assert.Contains("./NzbWebDAV --db-migration", script);
        Assert.Contains("./NzbWebDAV --encryption-maintenance", script);
        Assert.Contains("bootstrap_promote_master_key", script);
        Assert.DoesNotContain("BOOTSTRAP_SCRIPT=${", script);
        Assert.DoesNotContain("BACKEND_DIR=${", script);
        Assert.DoesNotContain("FRONTEND_DIR=${", script);
        Assert.Contains("FRONTEND_PUID=${FRONTEND_PUID:-1001}", script);
        Assert.Contains("su-exec \"$PUID:$PGID\" ./NzbWebDAV", script);
        Assert.Contains("su-exec \"$FRONTEND_PUID:$PGID\" env -i", script);
        Assert.DoesNotContain("su-exec \"$USER_NAME\"", script);
        Assert.DoesNotContain("su-exec \"$FRONTEND_USER\"", script);
        Assert.Contains("run_maintenance_child", script);
        Assert.Contains("clear_blank_master_key_override", script);
        Assert.Contains("INT) exit 130", script);
        Assert.Contains("TERM) exit 143", script);
        Assert.Contains("request_stop", script);
        Assert.Contains("finish_stop", script);
        Assert.Contains("secure_database_files", script);
        Assert.Contains("DATABASE_BASENAME=${DATABASE_BASENAME:-db.sqlite}", script);
        Assert.Contains("for DATABASE_SUFFIX in \"\" \"-wal\" \"-shm\"; do", script);
        Assert.Contains("PORT must be between 1 and 65535", script);
        Assert.Contains("NODE_ENV=\"$NODE_ENV\"", script);
        Assert.Contains("NZBDAV_FULL_STACK=\"$NZBDAV_FULL_STACK\"", script);
        Assert.Contains("SECURE_COOKIES=\"$SECURE_COOKIES\"", script);
        Assert.Contains("env -i", script);
        Assert.DoesNotContain("bootstrap_discard_master_stage", script);
    }
}

namespace backend.Tests.Deployment;

public sealed class EntrypointScriptTests
{
    [Fact]
    public void Entrypoint_CapturesMigrationExitCodeBeforeLoggingAndExit()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var script = File.ReadAllText(Path.Combine(repoRoot, "entrypoint.sh"));

        Assert.Contains("MIGRATION_EXIT_CODE=$?", script);
        Assert.Contains("echo \"Database migration failed. Exiting with error code $MIGRATION_EXIT_CODE.\"", script);
        Assert.Contains("exit $MIGRATION_EXIT_CODE", script);
    }
}

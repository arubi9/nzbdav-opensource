namespace backend.Tests.Deployment;

public sealed class EncryptionMaintenanceDispatchTests
{
    [Fact]
    public void EncryptionMaintenance_DispatchesInitializationAndStartupCheckOnce()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var source = File.ReadAllText(Path.Combine(repoRoot, "backend", "Program.cs"));
        var dispatchStart = source.IndexOf("if (args.Contains(\"--encryption-maintenance\"))", StringComparison.Ordinal);
        Assert.True(dispatchStart >= 0);
        var firstInitialize = source.IndexOf("await DatabaseInitialization", dispatchStart + 1, StringComparison.Ordinal);
        Assert.True(firstInitialize >= 0);
        var dispatchEnd = source.IndexOf("await DatabaseInitialization", firstInitialize + 1, StringComparison.Ordinal);
        Assert.True(dispatchEnd >= 0);
        var dispatch = source[dispatchStart..dispatchEnd];
        Assert.Contains("InitializeAsync(databaseContext", dispatch);
        Assert.Contains("StartupEncryptionCheck.RunAsync(databaseContext", dispatch);
        Assert.Equal(1, dispatch.Split("StartupEncryptionCheck.RunAsync", StringSplitOptions.None).Length - 1);
    }
}

using NzbWebDAV.Config;

namespace backend.Tests.Config;

[Collection(nameof(EnvironmentVariableCollection))]
public sealed class MultiNodeModeTests
{
    [Fact]
    public void IsEnabled_ReturnsFalse_WhenDatabaseUrlMissing()
    {
        using var environment = new TemporaryEnvironment(("DATABASE_URL", null));

        Assert.False(MultiNodeMode.IsEnabled);
    }

    [Fact]
    public void IsEnabled_ReturnsTrue_WhenDatabaseUrlPresent()
    {
        using var environment = new TemporaryEnvironment(("DATABASE_URL", "Host=pgbouncer;Database=nzbdav"));

        Assert.True(MultiNodeMode.IsEnabled);
    }
}

public sealed class TemporaryEnvironment : IDisposable
{
    private readonly Dictionary<string, string?> _previousValues;

    public TemporaryEnvironment(params (string Name, string? Value)[] variables)
    {
        _previousValues = variables
            .ToDictionary(x => x.Name, x => Environment.GetEnvironmentVariable(x.Name), StringComparer.Ordinal);

        foreach (var (name, value) in variables)
            Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose()
    {
        foreach (var (name, previousValue) in _previousValues)
            Environment.SetEnvironmentVariable(name, previousValue);
    }
}

[CollectionDefinition(nameof(EnvironmentVariableCollection), DisableParallelization = true)]
public sealed class EnvironmentVariableCollection;

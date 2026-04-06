using NzbWebDAV.Extensions;

namespace NzbWebDAV.Tests.Extensions;

public class StreamExtensionsTests
{
    [Fact]
    public async Task CopyToPooledAsyncReturnsExactBytes()
    {
        var input = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        await using var source = new MemoryStream(input);
        await using var destination = new MemoryStream();

        await source.CopyToPooledAsync(destination, bufferSize: 64);

        Assert.Equal(input, destination.ToArray());
    }

    [Fact]
    public async Task CopyRangeToPooledAsyncReturnsExactRangeBoundaries()
    {
        var input = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        await using var source = new MemoryStream(input);
        await using var destination = new MemoryStream();

        await source.CopyRangeToPooledAsync(destination, start: 5, end: 12, bufferSize: 4);

        Assert.Equal(input[5..13], destination.ToArray());
    }
}

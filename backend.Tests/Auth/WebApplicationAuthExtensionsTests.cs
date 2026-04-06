using Microsoft.AspNetCore.Http;
using NzbWebDAV.Auth;

namespace backend.Tests.Auth;

public sealed class WebApplicationAuthExtensionsTests
{
    [Theory]
    [InlineData("/", true)]
    [InlineData("/content/movies/file.mkv", true)]
    [InlineData("/metrics", false)]
    [InlineData("/health", false)]
    [InlineData("/api/meta/123", false)]
    [InlineData("/api/stream/123", false)]
    [InlineData("/ws", false)]
    public void ShouldUseWebdavBasicAuthentication_MatchesExpectedPaths(string path, bool expected)
    {
        Assert.Equal(expected, WebApplicationAuthExtensions.ShouldUseWebdavBasicAuthentication(new PathString(path)));
    }
}

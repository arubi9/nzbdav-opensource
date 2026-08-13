using System.Net;
using Microsoft.AspNetCore.Http;
using NzbWebDAV.Api.SabControllers.AddUrl;

namespace backend.Tests.Api.SabControllers;

public sealed class AddUrlRequestTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("192.0.2.1")]
    [InlineData("198.18.0.1")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    public void IsBlockedAddress_BlocksLocalAndPrivateRanges(string address)
    {
        Assert.True(AddUrlRequest.IsBlockedAddress(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("8.8.8.8")]
    [InlineData("2606:4700:4700::1111")]
    public void IsBlockedAddress_AllowsPublicRanges(string address)
    {
        Assert.False(AddUrlRequest.IsBlockedAddress(IPAddress.Parse(address)));
    }

    [Fact]
    public async Task ValidateRemoteUriAsync_RejectsNonHttpSchemes()
    {
        var ex = await Assert.ThrowsAsync<BadHttpRequestException>(
            () => AddUrlRequest.ValidateRemoteUriAsync("file:///etc/passwd"));

        Assert.Contains("scheme", ex.Message);
    }
}

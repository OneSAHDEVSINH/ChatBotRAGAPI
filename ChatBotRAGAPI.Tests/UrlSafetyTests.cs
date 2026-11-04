using System.Net;
using ChatBotRAGAPI.Services;

namespace ChatBotRAGAPI.Tests;

public class UrlSafetyTests
{
    [Theory]
    [InlineData("0.1.2.3")]
    [InlineData("10.0.0.1")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.255")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.0.0.8")]
    [InlineData("192.168.1.1")]
    [InlineData("198.18.0.1")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("ff02::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:10.1.2.3")]
    [InlineData("64:ff9b::a00:1")]
    public void Non_public_addresses_are_rejected(string ip) =>
        Assert.False(UrlSafety.IsPublicAddress(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("100.63.255.255")]
    [InlineData("100.128.0.1")]
    [InlineData("172.15.0.1")]
    [InlineData("172.32.0.1")]
    [InlineData("93.184.216.34")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("::ffff:8.8.8.8")]
    public void Public_addresses_are_allowed(string ip) =>
        Assert.True(UrlSafety.IsPublicAddress(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("http://example.com", true)]
    [InlineData("https://example.com", true)]
    [InlineData("ftp://example.com", false)]
    [InlineData("file:///c:/windows/win.ini", false)]
    public void Only_http_and_https_schemes_are_allowed(string url, bool allowed) =>
        Assert.Equal(allowed, UrlSafety.IsAllowedScheme(new Uri(url)));

    [Theory]
    [InlineData("http://127.0.0.1:1/")]
    [InlineData("http://localhost:1/")]
    [InlineData("http://[::1]:1/")]
    public async Task Handler_refuses_to_connect_to_non_public_hosts(string url)
    {
        using var client = new HttpClient(UrlSafety.CreateHandler());
        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync(url, TestContext.Current.CancellationToken));
        Assert.Contains("Blocked request to non-public address", ex.ToString());
    }
}

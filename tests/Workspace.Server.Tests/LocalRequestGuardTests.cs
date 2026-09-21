using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Workspace.Server;

namespace Workspace.Server.Tests;

public sealed class LocalRequestGuardTests
{
    [Theory]
    [InlineData("https://evil.example", false, false)]
    [InlineData("http://127.0.0.1:9999", false, false)]
    [InlineData("http://127.0.0.1:5080", false, true)]
    [InlineData("http://127.0.0.1:5174", false, false)]
    [InlineData("http://127.0.0.1:5174", true, true)]
    [InlineData("http://127.0.0.1.evil.example:5080", true, false)]
    [InlineData("null", true, false)]
    public void OriginsAreExplicitlyBounded(string origin, bool development, bool expected)
    {
        var context = Context();
        Assert.Equal(expected, LocalRequestGuard.IsAllowedOrigin(origin, context.Request, development));
    }

    [Fact]
    public async Task MutationsRequireTheLocalTokenAndJsonContentType()
    {
        var guard = Guard();
        var missing = Context();
        var reached = false;
        await guard.InvokeAsync(missing, _ => { reached = true; return Task.CompletedTask; });
        Assert.False(reached);
        Assert.Equal(403, missing.Response.StatusCode);

        var valid = Context();
        valid.Request.Headers["X-Workspace-Token"] = guard.Token;
        await guard.InvokeAsync(valid, _ => { reached = true; return Task.CompletedTask; });
        Assert.True(reached);
        Assert.Equal(200, valid.Response.StatusCode);

        var form = Context();
        form.Request.Headers["X-Workspace-Token"] = guard.Token;
        form.Request.ContentType = "application/x-www-form-urlencoded";
        await guard.InvokeAsync(form, _ => throw new InvalidOperationException("The form must not reach the endpoint."));
        Assert.Equal(415, form.Response.StatusCode);
    }

    [Fact]
    public async Task DnsRebindingRemotePeersAndCrossSiteFetchesAreRejected()
    {
        var guard = Guard();
        var host = Context();
        host.Request.Host = new("attacker.example", 5080);
        await guard.InvokeAsync(host, _ => throw new InvalidOperationException());
        Assert.Equal(403, host.Response.StatusCode);
        var remote = Context();
        remote.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.1");
        await guard.InvokeAsync(remote, _ => throw new InvalidOperationException());
        Assert.Equal(403, remote.Response.StatusCode);
        var crossSite = Context();
        crossSite.Request.Method = "GET";
        crossSite.Request.Headers["Sec-Fetch-Site"] = "cross-site";
        await guard.InvokeAsync(crossSite, _ => throw new InvalidOperationException());
        Assert.Equal(403, crossSite.Response.StatusCode);
    }

    private static DefaultHttpContext Context()
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new("127.0.0.1", 5080);
        context.Request.Scheme = "http";
        context.Request.Path = "/api/workspaces";
        context.Request.Method = "POST";
        context.Request.ContentType = "application/json";
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static LocalRequestGuard Guard() => new(new TestEnvironment(), NullLogger<LocalRequestGuard>.Instance);

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Workspace.Server";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

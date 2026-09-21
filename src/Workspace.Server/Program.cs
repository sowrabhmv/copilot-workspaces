using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Workspace.Server;
using Workspace.Server.Orchestration;
using Workspace.Server.Providers;

var checkProvider = args.Contains("--check-provider", StringComparer.Ordinal);
var builder = WebApplication.CreateBuilder(args.Where(a => a != "--check-provider").ToArray());
var applicationRoot = FindApplicationRoot(builder.Environment.ContentRootPath);
var options = new WorkspaceOptions();
builder.Configuration.GetSection("Workspaces").Bind(options);
options.DataDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(options.DataDirectory)
    ? Path.Combine(applicationRoot, ".data") : options.DataDirectory);
if (options.Port is < 1024 or > 65535)
    throw new InvalidOperationException("Workspaces:Port must be between 1024 and 65535.");
if (options.ProviderTimeoutSeconds is < 10 or > 600 ||
    options.DemoDelayMilliseconds is < 0 or > 5000)
    throw new InvalidOperationException("Workspace timing configuration is outside its supported range.");

builder.WebHost.ConfigureKestrel(server =>
{
    server.Listen(IPAddress.Loopback, options.Port);
    server.Limits.MaxRequestBodySize = 262144;
});
builder.Services.Configure<Microsoft.AspNetCore.Routing.RouteHandlerOptions>(routing =>
    routing.ThrowOnBadRequest = true);
builder.Services.ConfigureHttpJsonOptions(json =>
{
    json.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    json.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
    json.SerializerOptions.MaxDepth = 32;
});
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<IOptions<WorkspaceOptions>>(Options.Create(options));
builder.Services.AddSingleton<WorkspaceStore>();
builder.Services.AddSingleton<WorkspaceService>();
builder.Services.AddSingleton<LocalRequestGuard>();
builder.Services.AddSingleton<IAgentProvider, CopilotAgentProvider>();
builder.Services.AddSingleton<IAgentProvider, DemoAgentProvider>();
builder.Services.AddSingleton<IWorkspaceWorkflow, AgentWorkflow>();
builder.Services.AddSingleton<WorkspaceCoordinator>();
builder.Services.AddHostedService(services => services.GetRequiredService<WorkspaceCoordinator>());

var app = builder.Build();
if (checkProvider)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
    var provider = app.Services.GetServices<IAgentProvider>().Single(p => p.Name == "copilot");
    var status = await provider.GetStatusAsync(timeout.Token);
    Console.WriteLine(JsonSerializer.Serialize(status, new JsonSerializerOptions(JsonDefaults.Options) { WriteIndented = true }));
    Environment.ExitCode = status.State == "ready" ? 0 : 2;
    await app.DisposeAsync();
    return;
}

app.Use((context, next) => app.Services.GetRequiredService<LocalRequestGuard>().InvokeAsync(context, next));
app.MapWorkspaceApi();

var assetsDirectory = Path.Combine(applicationRoot, "web", "dist");
if (!Directory.Exists(assetsDirectory)) assetsDirectory = Path.Combine(AppContext.BaseDirectory, "wwwroot");
if (Directory.Exists(assetsDirectory))
{
    var assets = new PhysicalFileProvider(assetsDirectory);
    app.Lifetime.ApplicationStopped.Register(assets.Dispose);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = assets });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = assets });
    var indexFile = Path.Combine(assetsDirectory, "index.html");
    app.MapFallback(() => Results.File(indexFile, "text/html"));
}
else
{
    app.MapFallback(() => Results.Json(new ApiError("frontend_not_built",
        "Build the browser app with npm run build --prefix web, then restart. The API is available."),
        statusCode: StatusCodes.Status503ServiceUnavailable));
}

await app.RunAsync();

static string FindApplicationRoot(string contentRoot)
{
    foreach (var start in new[] { contentRoot, AppContext.BaseDirectory })
    {
        var directory = new DirectoryInfo(start);
        for (var depth = 0; directory is not null && depth < 7; depth++, directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Workspaces.slnx")))
                return directory.FullName;
    }
    return AppContext.BaseDirectory;
}

public partial class Program;

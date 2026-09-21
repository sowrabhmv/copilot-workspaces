using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Workspace.Server;

public sealed class LocalRequestGuard(IHostEnvironment environment, ILogger<LocalRequestGuard> logger)
{
    public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["X-Frame-Options"] = "DENY";
        context.Response.Headers["Content-Security-Policy"] =
            "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; " +
            "img-src 'self' data:; font-src 'self'; connect-src 'self'; object-src 'none'; " +
            "base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
        try
        {
            if (!IsLoopbackHost(context.Request.Host.Host) ||
                context.Connection.RemoteIpAddress is { } remote && !IPAddress.IsLoopback(remote))
                throw new WorkspaceException("local_only", "This application is available on loopback only.", 403);
            if (!context.Request.Path.StartsWithSegments("/api"))
            {
                await next(context);
                return;
            }

            context.Response.Headers.CacheControl = "no-store";
            if (context.Request.Headers["Sec-Fetch-Site"] == "cross-site")
                throw new WorkspaceException("cross_site_request", "Cross-site requests are not allowed.", 403);
            if (context.Request.Headers.TryGetValue("Origin", out var origin) &&
                !IsAllowedOrigin(origin.ToString(), context.Request, environment.IsDevelopment()))
                throw new WorkspaceException("untrusted_origin", "This request came from an untrusted browser origin.", 403);

            if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
            {
                if (!ValidToken(context.Request.Headers["X-Workspace-Token"].ToString()))
                    throw new WorkspaceException("invalid_token", "The local session changed. Reconnect before submitting again.", 403);
                if (!context.Request.HasJsonContentType())
                    throw new WorkspaceException("json_required", "Send this command as application/json.", 415);
            }
            await next(context);
        }
        catch (WorkspaceException error) when (!context.Response.HasStarted)
        {
            await Error(context, error.StatusCode, error.Code, error.Message);
        }
        catch (BadHttpRequestException error) when (!context.Response.HasStarted)
        {
            await Error(context, error.StatusCode, "invalid_request", "The request is malformed or exceeds the allowed size.");
        }
        catch (System.Text.Json.JsonException) when (!context.Response.HasStarted)
        {
            await Error(context, 400, "invalid_json", "The JSON body does not match the application contract.");
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // A disconnected browser does not cancel any durable agent job.
        }
        catch (Exception error) when (!context.Response.HasStarted)
        {
            logger.LogError("HTTP request failed with {ErrorType}.", error.GetType().Name);
            await Error(context, 500, "service_error", "The local service could not complete this request. Your saved work has not been reset.");
        }
    }

    public static bool IsLoopbackHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address);

    public static bool IsAllowedOrigin(string origin, HttpRequest request, bool allowDevelopment)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 ||
            uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            return false;
        var sameOrigin = string.Equals(uri.Host, request.Host.Host.Trim('[', ']'), StringComparison.OrdinalIgnoreCase) &&
                         uri.Port == (request.Host.Port ?? (request.Scheme == "https" ? 443 : 80)) &&
                         uri.Scheme == request.Scheme;
        return sameOrigin || allowDevelopment && uri.Scheme == "http" && uri.Port == 5174 &&
            uri.Host is "127.0.0.1" or "localhost";
    }

    private bool ValidToken(string supplied) =>
        supplied.Length == Token.Length &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(supplied), Encoding.ASCII.GetBytes(Token));

    private static Task Error(HttpContext context, int status, string code, string message)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new ApiError(code, message), context.RequestAborted);
    }
}

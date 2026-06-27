using Microsoft.Net.Http.Headers;
using ModelContextProtocol.Server;
using SimpleAuth.ResourceServer.Services;
using SimpleAuth.ResourceServer.Tools;

var port = ResolvePort(args, 8001);
var authServerUrl = ResolveArgument(args, "--auth-server") ?? Environment.GetEnvironmentVariable("AUTH_SERVER_URL") ?? "http://localhost:9000";
var baseUrl = $"http://localhost:{port}";

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(baseUrl);
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.SetIsOriginAllowed(static origin =>
                origin.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase) ||
                origin.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase))
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});
builder.Services.AddSingleton(new TokenVerifierService($"{authServerUrl.TrimEnd('/')}/introspect"));
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithTools<McpTools>();

var app = builder.Build();
app.UseCors();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/mcp", StringComparison.OrdinalIgnoreCase) &&
        !HttpMethods.IsOptions(context.Request.Method))
    {
        if (!context.Request.Headers.TryGetValue(HeaderNames.Authorization, out var headerValue) ||
            !headerValue.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.Append(HeaderNames.WWWAuthenticate, $"{"Be" + "arer"} resource=\"{baseUrl}\"");
            await context.Response.WriteAsJsonAsync(new { error = "missing_bearer_token" });
            return;
        }

        var token = headerValue.ToString()["Bearer ".Length..].Trim();
        var verifier = context.RequestServices.GetRequiredService<TokenVerifierService>();
        var tokenInfo = await verifier.VerifyTokenAsync(token, context.RequestAborted);
        if (tokenInfo is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.Append(HeaderNames.WWWAuthenticate, $"{"Be" + "arer"} resource=\"{baseUrl}\", authorization_uri=\"{authServerUrl}\"");
            await context.Response.WriteAsJsonAsync(new { error = "invalid_token" });
            return;
        }

        context.Items["token_info"] = tokenInfo;
    }

    await next();
});

app.MapGet("/.well-known/oauth-protected-resource", () => Results.Json(new
{
    resource = baseUrl,
    authorization_servers = new[] { authServerUrl },
    bearer_methods_supported = new[] { "header" }
}));

app.MapMcp("/mcp");
app.Run();
return;

static int ResolvePort(string[] args, int fallback)
{
    var explicitPort = ResolveArgument(args, "--port");
    if (int.TryParse(explicitPort, out var parsedPort))
    {
        return parsedPort;
    }

    var envPort = Environment.GetEnvironmentVariable("PORT");
    return int.TryParse(envPort, out var port) ? port : fallback;
}

static string? ResolveArgument(string[] args, string name)
{
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i] == name && i + 1 < args.Length)
        {
            return args[i + 1];
        }
    }

    return null;
}

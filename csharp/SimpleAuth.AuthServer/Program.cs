using System.Net;
using Microsoft.AspNetCore.WebUtilities;
using SimpleAuth.AuthServer.Models;
using SimpleAuth.AuthServer.Services;

var port = ResolvePort(args, 9000);
var baseUrl = $"http://localhost:{port}";

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(baseUrl);
builder.Services.AddSingleton<OAuthProviderService>();
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

var app = builder.Build();
app.UseCors();

app.MapGet("/.well-known/oauth-authorization-server", () => Results.Json(new
{
    issuer = baseUrl,
    authorization_endpoint = $"{baseUrl}/authorize",
    token_endpoint = $"{baseUrl}/token",
    registration_endpoint = $"{baseUrl}/register",
    introspection_endpoint = $"{baseUrl}/introspect",
    revocation_endpoint = $"{baseUrl}/revoke",
    response_types_supported = new[] { "code" },
    grant_types_supported = new[] { "authorization_code" },
    code_challenge_methods_supported = new[] { "S256" },
    scopes_supported = new[] { "user" }
}));

app.MapPost("/register", async (HttpRequest request, OAuthProviderService provider) =>
{
    var registration = await request.ReadFromJsonAsync<ClientRegistrationRequest>();
    if (registration is null || registration.RedirectUris is null || registration.RedirectUris.Count == 0)
    {
        return Results.BadRequest(new { error = "invalid_client_metadata", error_description = "A client_name and at least one redirect_uri are required." });
    }

    var client = provider.RegisterClient(registration);
    return Results.Json(new
    {
        client_id = client.ClientId,
        client_secret = client.ClientSecret,
        client_name = client.ClientName,
        redirect_uris = client.RedirectUris,
        grant_types = client.GrantTypes,
        response_types = client.ResponseTypes,
        token_endpoint_auth_method = client.TokenEndpointAuthMethod
    });
});

app.MapGet("/authorize", (HttpRequest request, OAuthProviderService provider) =>
{
    var clientId = request.Query["client_id"].ToString();
    var responseType = request.Query["response_type"].ToString();
    var redirectUri = request.Query["redirect_uri"].ToString();
    var codeChallenge = request.Query["code_challenge"].ToString();
    var codeChallengeMethod = request.Query["code_challenge_method"].ToString();
    var state = string.IsNullOrWhiteSpace(request.Query["state"].ToString()) ? Guid.NewGuid().ToString("N") : request.Query["state"].ToString();
    var resource = request.Query["resource"].ToString();

    if (!string.Equals(responseType, "code", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(codeChallenge))
    {
        return Results.BadRequest(new { error = "invalid_request", error_description = "client_id, response_type=code, and code_challenge are required." });
    }

    if (!string.Equals(codeChallengeMethod, "S256", StringComparison.Ordinal))
    {
        return Results.BadRequest(new { error = "invalid_request", error_description = "Only S256 PKCE is supported." });
    }

    var client = provider.GetClient(clientId);
    if (client is null)
    {
        return Results.BadRequest(new { error = "unauthorized_client", error_description = "Unknown client_id." });
    }

    var redirectUriProvidedExplicitly = !string.IsNullOrWhiteSpace(redirectUri);
    if (!redirectUriProvidedExplicitly)
    {
        if (client.RedirectUris.Count != 1)
        {
            return Results.BadRequest(new { error = "invalid_request", error_description = "redirect_uri is required for clients with multiple redirect URIs." });
        }

        redirectUri = client.RedirectUris[0];
    }

    if (!client.RedirectUris.Contains(redirectUri, StringComparer.Ordinal))
    {
        return Results.BadRequest(new { error = "invalid_request", error_description = "redirect_uri is not registered for this client." });
    }

    provider.StoreAuthorizationRequest(state, new StateData(
        RedirectUri: redirectUri,
        CodeChallenge: codeChallenge,
        RedirectUriProvidedExplicitly: redirectUriProvidedExplicitly,
        ClientId: clientId,
        Resource: string.IsNullOrWhiteSpace(resource) ? null : resource));

    var loginUrl = QueryHelpers.AddQueryString($"{baseUrl}/login", new Dictionary<string, string?>
    {
        ["state"] = state,
        ["client_id"] = clientId
    });

    return Results.Redirect(loginUrl);
});

app.MapGet("/login", (HttpRequest request) =>
{
    var state = request.Query["state"].ToString();
    if (string.IsNullOrWhiteSpace(state))
    {
        return Results.BadRequest("Missing state parameter.");
    }

    return Results.Content(RenderLoginPage(baseUrl, state), "text/html");
});

app.MapPost("/login/callback", async (HttpRequest request, OAuthProviderService provider) =>
{
    var form = await request.ReadFormAsync();
    var username = form["username"].ToString();
    var password = form["password"].ToString();
    var state = form["state"].ToString();

    if (!provider.ValidateCredentials(username, password))
    {
        return Results.Content(
            RenderLoginPage(baseUrl, state, "Invalid credentials. Use the demo account shown on the page."),
            "text/html",
            statusCode: StatusCodes.Status401Unauthorized);
    }

    var stateData = provider.GetAuthorizationState(state);
    if (stateData is null)
    {
        return Results.BadRequest("Invalid state parameter.");
    }

    var client = provider.GetClient(stateData.ClientId);
    var consentToken = provider.CreatePendingConsent(username, state, client?.ClientName ?? "Unknown Application");
    return Results.Redirect(QueryHelpers.AddQueryString($"{baseUrl}/consent", "token", consentToken));
});

app.MapGet("/consent", (HttpRequest request, OAuthProviderService provider) =>
{
    var consentToken = request.Query["token"].ToString();
    if (string.IsNullOrWhiteSpace(consentToken))
    {
        return Results.BadRequest("Missing consent token.");
    }

    var consent = provider.GetPendingConsent(consentToken);
    if (consent is null)
    {
        return Results.BadRequest("Invalid consent token.");
    }

    return Results.Content(RenderConsentPage(baseUrl, consentToken, consent), "text/html");
});

app.MapPost("/consent/callback", async (HttpRequest request, OAuthProviderService provider) =>
{
    var form = await request.ReadFormAsync();
    var consentToken = form["consent_token"].ToString();
    var action = form["action"].ToString();

    if (string.Equals(action, "deny", StringComparison.OrdinalIgnoreCase))
    {
        var deniedState = provider.DenyConsent(consentToken);
        if (deniedState is not null)
        {
            var deniedRedirect = QueryHelpers.AddQueryString(deniedState.RedirectUri, new Dictionary<string, string?>
            {
                ["error"] = "access_denied"
            });
            return Results.Redirect(deniedRedirect);
        }

        return Results.Content(RenderAccessDeniedPage(), "text/html", statusCode: StatusCodes.Status403Forbidden);
    }

    var approval = provider.ApproveConsent(consentToken);
    if (approval is null)
    {
        return Results.BadRequest("Invalid consent token.");
    }

    var redirectUrl = QueryHelpers.AddQueryString(approval.Value.StateData.RedirectUri, new Dictionary<string, string?>
    {
        ["code"] = approval.Value.AuthCode.Code,
        ["state"] = approval.Value.State
    });

    return Results.Redirect(redirectUrl);
});

app.MapPost("/token", async (HttpRequest request, OAuthProviderService provider) =>
{
    var form = await request.ReadFormAsync();
    var grantType = form["grant_type"].ToString();
    var code = form["code"].ToString();
    var redirectUri = form["redirect_uri"].ToString();
    var clientId = form["client_id"].ToString();
    var clientSecret = form["client_secret"].ToString();
    var codeVerifier = form["code_verifier"].ToString();

    if (!string.Equals(grantType, "authorization_code", StringComparison.Ordinal) ||
        string.IsNullOrWhiteSpace(code) ||
        string.IsNullOrWhiteSpace(clientId) ||
        string.IsNullOrWhiteSpace(clientSecret) ||
        string.IsNullOrWhiteSpace(codeVerifier))
    {
        return Results.BadRequest(new { error = "unsupported_grant_type", error_description = "Only authorization_code with PKCE is supported." });
    }

    if (!provider.TryExchangeAuthorizationCode(code, clientId, clientSecret, redirectUri, codeVerifier, out var accessToken, out var error, out var errorDescription))
    {
        return Results.BadRequest(new { error, error_description = errorDescription });
    }

    return Results.Json(new
    {
        access_token = accessToken!.Token,
        token_type = "Bearer",
        expires_in = 3600,
        scope = string.Join(' ', accessToken.Scopes)
    });
});

app.MapPost("/revoke", async (HttpRequest request, OAuthProviderService provider) =>
{
    var form = await request.ReadFormAsync();
    var token = form["token"].ToString();
    if (!string.IsNullOrWhiteSpace(token))
    {
        provider.RevokeToken(token);
    }

    return Results.NoContent();
});

app.MapPost("/introspect", async (HttpRequest request, OAuthProviderService provider) =>
{
    var form = await request.ReadFormAsync();
    var token = form["token"].ToString();
    if (string.IsNullOrWhiteSpace(token))
    {
        return Results.Json(new { active = false }, statusCode: StatusCodes.Status400BadRequest);
    }

    var accessToken = provider.GetActiveToken(token);
    if (accessToken is null)
    {
        return Results.Json(new { active = false });
    }

    return Results.Json(new
    {
        active = true,
        client_id = accessToken.ClientId,
        scope = string.Join(' ', accessToken.Scopes),
        exp = accessToken.ExpiresAt,
        token_type = "Bearer",
        aud = accessToken.Resource
    });
});

app.Run();
return;

static int ResolvePort(string[] args, int fallback)
{
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i] == "--port" && i + 1 < args.Length && int.TryParse(args[i + 1], out var explicitPort))
        {
            return explicitPort;
        }
    }

    var envPort = Environment.GetEnvironmentVariable("PORT");
    return int.TryParse(envPort, out var port) ? port : fallback;
}

static string RenderLoginPage(string baseUrl, string state, string? error = null) => $$"""
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="utf-8" />
    <title>SimpleAuth Login</title>
    <style>
        body { font-family: Arial, sans-serif; max-width: 480px; margin: 48px auto; color: #222; }
        form { border: 1px solid #ddd; border-radius: 12px; padding: 24px; }
        label { display: block; margin-top: 12px; }
        input { width: 100%; padding: 10px; margin-top: 6px; box-sizing: border-box; }
        button { margin-top: 18px; padding: 10px 16px; }
        .error { color: #b00020; margin-top: 12px; }
        code { background: #f5f5f5; padding: 2px 4px; }
    </style>
</head>
<body>
    <h1>Sign in to SimpleAuth</h1>
    <p>Demo credentials: <code>devloper_harsh</code> / <code>admin@2000</code></p>
    <form method="post" action="{{baseUrl}}/login/callback">
        <input type="hidden" name="state" value="{{WebUtility.HtmlEncode(state)}}" />
        <label>Username <input name="username" value="devloper_harsh" /></label>
        <label>Password <input type="password" name="password" value="admin@2000" /></label>
        <button type="submit">Continue</button>
        {{(string.IsNullOrWhiteSpace(error) ? string.Empty : $"<p class=\"error\">{WebUtility.HtmlEncode(error)}</p>")}}
    </form>
</body>
</html>
""";

static string RenderConsentPage(string baseUrl, string consentToken, ConsentData consent) => $$"""
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="utf-8" />
    <title>SimpleAuth Consent</title>
    <style>
        body { font-family: Arial, sans-serif; max-width: 700px; margin: 48px auto; color: #222; }
        .card { border: 1px solid #ddd; border-radius: 12px; padding: 24px; }
        ul { line-height: 1.7; }
        button { padding: 10px 16px; margin-right: 12px; }
    </style>
</head>
<body>
    <div class="card">
        <h1>Approve access</h1>
        <p><strong>{{WebUtility.HtmlEncode(consent.ClientName)}}</strong> wants to access your MCP tools as <strong>{{WebUtility.HtmlEncode(consent.Username)}}</strong>.</p>
        <p>Requested scope: <code>user</code></p>
        <h2>Available tools</h2>
        <ul>
            <li><strong>get_time</strong> — returns the current server time</li>
            <li><strong>calculator</strong> — evaluates math expressions</li>
            <li><strong>get_weather</strong> — returns simulated weather data</li>
        </ul>
        <form method="post" action="{{baseUrl}}/consent/callback">
            <input type="hidden" name="consent_token" value="{{WebUtility.HtmlEncode(consentToken)}}" />
            <button type="submit" name="action" value="approve">Approve</button>
            <button type="submit" name="action" value="deny">Deny</button>
        </form>
    </div>
</body>
</html>
""";

static string RenderAccessDeniedPage() => """
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="utf-8" />
    <title>Access denied</title>
</head>
<body style="font-family: Arial, sans-serif; max-width: 520px; margin: 48px auto;">
    <h1>Access denied</h1>
    <p>The authorization request was not approved.</p>
</body>
</html>
""";

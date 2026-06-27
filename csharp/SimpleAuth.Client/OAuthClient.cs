using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace SimpleAuth.Client;

public sealed class OAuthMcpClient
{
    private const string RedirectUri = "http://localhost:3030/callback";
    private readonly string _resourceServerUrl;
    private readonly HttpClient _httpClient = new();
    private readonly ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;

    public OAuthMcpClient(string resourceServerUrl)
    {
        _resourceServerUrl = resourceServerUrl.TrimEnd('/');
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        await using var callbackServer = new CallbackServer();
        await callbackServer.StartAsync();

        var discovery = await DiscoverAsync(_resourceServerUrl, cancellationToken);
        var registration = await RegisterClientAsync(discovery.AuthorizationServer.RegistrationEndpoint, cancellationToken);

        var state = Guid.NewGuid().ToString("N");
        var codeVerifier = GenerateCodeVerifier();
        var authorizationUrl = BuildAuthorizationUrl(
            discovery.AuthorizationServer.AuthorizationEndpoint,
            registration.ClientId,
            RedirectUri,
            state,
            GenerateCodeChallenge(codeVerifier),
            discovery.ResourceMetadata.Resource);

        OpenBrowser(authorizationUrl);
        Console.WriteLine($"Open this URL if the browser does not launch automatically:\n{authorizationUrl}\n");

        var callback = await callbackServer.WaitForCallbackAsync(TimeSpan.FromMinutes(5), cancellationToken);
        if (!string.IsNullOrWhiteSpace(callback.Error))
        {
            throw new InvalidOperationException($"Authorization failed: {callback.Error}");
        }

        if (!string.Equals(callback.State, state, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("OAuth state mismatch.");
        }

        var token = await ExchangeCodeAsync(
            discovery.AuthorizationServer.TokenEndpoint,
            registration.ClientId,
            registration.ClientSecret,
            callback.Code ?? throw new InvalidOperationException("Missing authorization code."),
            codeVerifier,
            RedirectUri,
            cancellationToken);

        await using var client = await ConnectAsync(token.AccessToken, cancellationToken);
        await RunInteractiveLoopAsync(client, cancellationToken);
    }

    public async Task<DiscoveryBundle> DiscoverAsync(string serverUrl, CancellationToken cancellationToken = default)
    {
        var resourceMetadata = await _httpClient.GetFromJsonAsync<ProtectedResourceMetadata>($"{serverUrl}/.well-known/oauth-protected-resource", cancellationToken)
            ?? throw new InvalidOperationException("Protected resource metadata was not returned.");

        var authorizationServerUrl = resourceMetadata.AuthorizationServers.FirstOrDefault()
            ?? throw new InvalidOperationException("No authorization_servers were advertised by the resource server.");

        var authorizationServer = await _httpClient.GetFromJsonAsync<AuthorizationServerMetadata>($"{authorizationServerUrl.TrimEnd('/')}/.well-known/oauth-authorization-server", cancellationToken)
            ?? throw new InvalidOperationException("Authorization server discovery failed.");

        return new DiscoveryBundle(resourceMetadata, authorizationServer);
    }

    public async Task<ClientRegistrationResponse> RegisterClientAsync(string registrationEndpoint, CancellationToken cancellationToken = default)
    {
        var request = new ClientRegistrationRequest(
            ClientName: "SimpleAuth C# Client",
            RedirectUris: [RedirectUri],
            GrantTypes: ["authorization_code"],
            ResponseTypes: ["code"],
            TokenEndpointAuthMethod: "client_secret_post");

        using var response = await _httpClient.PostAsJsonAsync(registrationEndpoint, request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ClientRegistrationResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Client registration returned no payload.");
    }

    public string BuildAuthorizationUrl(
        string authorizationEndpoint,
        string clientId,
        string redirectUri,
        string state,
        string codeChallenge,
        string? resource = null)
    {
        var parameters = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["scope"] = "user",
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
            ["resource"] = resource
        };

        var query = string.Join("&", parameters
            .Where(static pair => !string.IsNullOrWhiteSpace(pair.Value))
            .Select(static pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value!)}"));
        return $"{authorizationEndpoint}?{query}";
    }

    public async Task<TokenResponse> ExchangeCodeAsync(
        string tokenEndpoint,
        string clientId,
        string clientSecret,
        string code,
        string codeVerifier,
        string redirectUri,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsync(tokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["code_verifier"] = codeVerifier
        }), cancellationToken);

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Token endpoint returned no payload.");
    }

    private async Task<McpClient> ConnectAsync(string accessToken, CancellationToken cancellationToken)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri($"{_resourceServerUrl}/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string>
            {
                ["Authorization"] = $"{("Be" + "arer")} {accessToken}"
            }
        }, _httpClient, _loggerFactory, false);

        return await McpClient.CreateAsync(
            transport,
            new McpClientOptions
            {
                ClientInfo = new Implementation { Name = "SimpleAuth.Client", Version = "1.0.0" },
                InitializationTimeout = TimeSpan.FromSeconds(30)
            },
            _loggerFactory,
            cancellationToken);
    }

    private static string GenerateCodeVerifier() => Base64UrlEncode(RandomNumberGenerator.GetBytes(48));

    private static string GenerateCodeChallenge(string verifier) => Base64UrlEncode(SHA256.HashData(Encoding.UTF8.GetBytes(verifier)));

    private static string Base64UrlEncode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static void OpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch
        {
        }
    }

    private static async Task RunInteractiveLoopAsync(McpClient client, CancellationToken cancellationToken)
    {
        Console.WriteLine("Connected to the MCP resource server.");
        Console.WriteLine("Commands: list | call <tool> [json-args] | quit");

        while (true)
        {
            Console.Write("mcp> ");
            var input = Console.ReadLine();
            if (input is null)
            {
                break;
            }

            var command = input.Trim();
            if (command.Length == 0)
            {
                continue;
            }

            if (string.Equals(command, "quit", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (string.Equals(command, "list", StringComparison.OrdinalIgnoreCase))
            {
                var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
                foreach (var tool in tools)
                {
                    Console.WriteLine($"- {tool.Name}: {tool.Description}");
                }

                continue;
            }

            if (command.StartsWith("call ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = command.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                {
                    Console.WriteLine("Usage: call <tool> [json-args]");
                    continue;
                }

                IReadOnlyDictionary<string, object?> arguments = new Dictionary<string, object?>();
                if (parts.Length == 3)
                {
                    arguments = ParseArguments(parts[2]);
                }

                var result = await client.CallToolAsync(parts[1], arguments, cancellationToken: cancellationToken);
                Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
                continue;
            }

            Console.WriteLine("Unknown command.");
        }
    }

    private static IReadOnlyDictionary<string, object?> ParseArguments(string json)
    {
        var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, JsonOptions)
            ?? new Dictionary<string, JsonElement>();

        return payload.ToDictionary(static pair => pair.Key, static pair => (object?)pair.Value);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };
}

public sealed record DiscoveryBundle(ProtectedResourceMetadata ResourceMetadata, AuthorizationServerMetadata AuthorizationServer);

public sealed record ProtectedResourceMetadata(
    [property: JsonPropertyName("resource")] string Resource,
    [property: JsonPropertyName("authorization_servers")] List<string> AuthorizationServers,
    [property: JsonPropertyName("bearer_methods_supported")] List<string> BearerMethodsSupported);

public sealed record AuthorizationServerMetadata(
    [property: JsonPropertyName("issuer")] string Issuer,
    [property: JsonPropertyName("authorization_endpoint")] string AuthorizationEndpoint,
    [property: JsonPropertyName("token_endpoint")] string TokenEndpoint,
    [property: JsonPropertyName("registration_endpoint")] string RegistrationEndpoint,
    [property: JsonPropertyName("introspection_endpoint")] string IntrospectionEndpoint,
    [property: JsonPropertyName("revocation_endpoint")] string RevocationEndpoint);

public sealed record ClientRegistrationRequest(
    [property: JsonPropertyName("client_name")] string ClientName,
    [property: JsonPropertyName("redirect_uris")] List<string> RedirectUris,
    [property: JsonPropertyName("grant_types")] List<string> GrantTypes,
    [property: JsonPropertyName("response_types")] List<string> ResponseTypes,
    [property: JsonPropertyName("token_endpoint_auth_method")] string TokenEndpointAuthMethod);

public sealed record ClientRegistrationResponse(
    [property: JsonPropertyName("client_id")] string ClientId,
    [property: JsonPropertyName("client_secret")] string ClientSecret);

public sealed record TokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("scope")] string Scope);

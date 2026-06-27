using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace SimpleAuth.ResourceServer.Services;

public sealed class TokenVerifierService
{
    private readonly HttpClient _httpClient;
    private readonly string _introspectionEndpoint;

    public TokenVerifierService(string introspectionEndpoint = "http://localhost:9000/introspect")
    {
        _httpClient = new HttpClient();
        _introspectionEndpoint = introspectionEndpoint;
    }

    public async Task<TokenInfo?> VerifyTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsync(
            _introspectionEndpoint,
            new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token }),
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var payload = await response.Content.ReadFromJsonAsync<IntrospectionResponse>(cancellationToken: cancellationToken);
        if (payload is null || !payload.Active)
        {
            return null;
        }

        return new TokenInfo(
            payload.ClientId ?? string.Empty,
            string.IsNullOrWhiteSpace(payload.Scope) ? [] : payload.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            payload.Exp ?? 0,
            payload.TokenType ?? "Bearer");
    }

    private sealed record IntrospectionResponse(
        [property: JsonPropertyName("active")] bool Active,
        [property: JsonPropertyName("client_id")] string? ClientId,
        [property: JsonPropertyName("scope")] string? Scope,
        [property: JsonPropertyName("exp")] long? Exp,
        [property: JsonPropertyName("token_type")] string? TokenType);
}

public sealed record TokenInfo(string ClientId, IReadOnlyList<string> Scopes, long ExpiresAt, string TokenType);

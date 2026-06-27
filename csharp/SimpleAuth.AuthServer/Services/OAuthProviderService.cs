using System.Security.Cryptography;
using System.Text;
using SimpleAuth.AuthServer.Models;

namespace SimpleAuth.AuthServer.Services;

public sealed class OAuthProviderService
{
    private const int AuthorizationCodeLifetimeSeconds = 300;
    private const int AccessTokenLifetimeSeconds = 3600;
    private readonly object _gate = new();

    public Dictionary<string, ClientInfo> Clients { get; } = new();
    public Dictionary<string, AuthCode> AuthCodes { get; } = new();
    public Dictionary<string, AccessToken> Tokens { get; } = new();
    public Dictionary<string, StateData> StateMapping { get; } = new();
    public Dictionary<string, ConsentData> PendingConsent { get; } = new();

    public string DemoUsername => "devloper_harsh";
    public string DemoPassword => "admin@2000";
    public string McpScope => "user";

    public ClientInfo RegisterClient(ClientRegistrationRequest request)
    {
        var client = new ClientInfo(
            ClientId: GenerateToken(),
            ClientSecret: GenerateToken(),
            ClientName: string.IsNullOrWhiteSpace(request.ClientName) ? "SimpleAuth Client" : request.ClientName.Trim(),
            RedirectUris: request.RedirectUris?.Where(static uri => !string.IsNullOrWhiteSpace(uri)).Distinct(StringComparer.Ordinal).ToList() ?? [],
            GrantTypes: request.GrantTypes?.Where(static value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToList() ?? ["authorization_code"],
            ResponseTypes: request.ResponseTypes?.Where(static value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToList() ?? ["code"],
            TokenEndpointAuthMethod: string.IsNullOrWhiteSpace(request.TokenEndpointAuthMethod) ? "client_secret_post" : request.TokenEndpointAuthMethod.Trim());

        lock (_gate)
        {
            Clients[client.ClientId] = client;
        }

        return client;
    }

    public ClientInfo? GetClient(string clientId)
    {
        lock (_gate)
        {
            return Clients.GetValueOrDefault(clientId);
        }
    }

    public bool ValidateCredentials(string? username, string? password) =>
        string.Equals(username, DemoUsername, StringComparison.Ordinal) &&
        string.Equals(password, DemoPassword, StringComparison.Ordinal);

    public void StoreAuthorizationRequest(string state, StateData stateData)
    {
        lock (_gate)
        {
            StateMapping[state] = stateData;
        }
    }

    public StateData? GetAuthorizationState(string state)
    {
        lock (_gate)
        {
            return StateMapping.GetValueOrDefault(state);
        }
    }

    public string CreatePendingConsent(string username, string state, string clientName)
    {
        var token = $"consent_{GenerateToken()}";
        var consent = new ConsentData(username, state, clientName, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        lock (_gate)
        {
            PendingConsent[token] = consent;
        }

        return token;
    }

    public ConsentData? GetPendingConsent(string consentToken)
    {
        lock (_gate)
        {
            return PendingConsent.GetValueOrDefault(consentToken);
        }
    }

    public (AuthCode AuthCode, StateData StateData, string State)? ApproveConsent(string consentToken)
    {
        lock (_gate)
        {
            if (!PendingConsent.Remove(consentToken, out var consent) || !StateMapping.TryGetValue(consent.State, out var stateData))
            {
                return null;
            }

            var authCode = new AuthCode(
                Code: GenerateToken(),
                ClientId: stateData.ClientId,
                RedirectUri: stateData.RedirectUri,
                RedirectUriProvidedExplicitly: stateData.RedirectUriProvidedExplicitly,
                ExpiresAt: DateTimeOffset.UtcNow.AddSeconds(AuthorizationCodeLifetimeSeconds).ToUnixTimeSeconds(),
                Scopes: [McpScope],
                CodeChallenge: stateData.CodeChallenge,
                Resource: stateData.Resource);

            AuthCodes[authCode.Code] = authCode;
            return (authCode, stateData, consent.State);
        }
    }

    public StateData? DenyConsent(string consentToken)
    {
        lock (_gate)
        {
            if (!PendingConsent.Remove(consentToken, out var consent))
            {
                return null;
            }

            return StateMapping.GetValueOrDefault(consent.State);
        }
    }

    public bool TryExchangeAuthorizationCode(
        string code,
        string clientId,
        string clientSecret,
        string? redirectUri,
        string codeVerifier,
        out AccessToken? accessToken,
        out string error,
        out string errorDescription)
    {
        accessToken = null;
        error = "invalid_grant";
        errorDescription = "The authorization code is invalid.";

        lock (_gate)
        {
            if (!Clients.TryGetValue(clientId, out var client) || !string.Equals(client.ClientSecret, clientSecret, StringComparison.Ordinal))
            {
                error = "invalid_client";
                errorDescription = "Client authentication failed.";
                return false;
            }

            if (!AuthCodes.TryGetValue(code, out var authCode))
            {
                return false;
            }

            if (!string.Equals(authCode.ClientId, clientId, StringComparison.Ordinal))
            {
                errorDescription = "The authorization code was not issued to this client.";
                return false;
            }

            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > authCode.ExpiresAt)
            {
                AuthCodes.Remove(code);
                errorDescription = "The authorization code has expired.";
                return false;
            }

            if (authCode.RedirectUriProvidedExplicitly && !string.Equals(authCode.RedirectUri, redirectUri, StringComparison.Ordinal))
            {
                errorDescription = "The redirect_uri does not match the authorization request.";
                return false;
            }

            if (!string.Equals(ComputeCodeChallenge(codeVerifier), authCode.CodeChallenge, StringComparison.Ordinal))
            {
                errorDescription = "PKCE verification failed.";
                return false;
            }

            AuthCodes.Remove(code);
            accessToken = new AccessToken(
                Token: GenerateToken(),
                ClientId: clientId,
                Scopes: authCode.Scopes,
                ExpiresAt: DateTimeOffset.UtcNow.AddSeconds(AccessTokenLifetimeSeconds).ToUnixTimeSeconds(),
                Resource: authCode.Resource);
            Tokens[accessToken.Token] = accessToken;
            return true;
        }
    }

    public void RevokeToken(string token)
    {
        lock (_gate)
        {
            Tokens.Remove(token);
        }
    }

    public AccessToken? GetActiveToken(string token)
    {
        lock (_gate)
        {
            if (!Tokens.TryGetValue(token, out var accessToken))
            {
                return null;
            }

            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > accessToken.ExpiresAt)
            {
                Tokens.Remove(token);
                return null;
            }

            return accessToken;
        }
    }

    private static string GenerateToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private static string ComputeCodeChallenge(string codeVerifier)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(codeVerifier));
        return Base64UrlEncode(hash);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

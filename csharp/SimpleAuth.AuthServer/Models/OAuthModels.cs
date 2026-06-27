using System.Text.Json.Serialization;

namespace SimpleAuth.AuthServer.Models;

public record ClientInfo(
    string ClientId,
    string ClientSecret,
    string ClientName,
    List<string> RedirectUris,
    List<string> GrantTypes,
    List<string> ResponseTypes,
    string TokenEndpointAuthMethod);

public record AuthCode(
    string Code,
    string ClientId,
    string RedirectUri,
    bool RedirectUriProvidedExplicitly,
    double ExpiresAt,
    List<string> Scopes,
    string CodeChallenge,
    string? Resource);

public record AccessToken(
    string Token,
    string ClientId,
    List<string> Scopes,
    long ExpiresAt,
    string? Resource);

public record StateData(
    string RedirectUri,
    string CodeChallenge,
    bool RedirectUriProvidedExplicitly,
    string ClientId,
    string? Resource);

public record ConsentData(
    string Username,
    string State,
    string ClientName,
    double AuthenticatedAt);

public sealed record ClientRegistrationRequest(
    [property: JsonPropertyName("client_name")] string? ClientName,
    [property: JsonPropertyName("redirect_uris")] List<string>? RedirectUris,
    [property: JsonPropertyName("grant_types")] List<string>? GrantTypes,
    [property: JsonPropertyName("response_types")] List<string>? ResponseTypes,
    [property: JsonPropertyName("token_endpoint_auth_method")] string? TokenEndpointAuthMethod);

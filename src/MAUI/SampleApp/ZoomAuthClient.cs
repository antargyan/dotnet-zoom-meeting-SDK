using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace SampleApp;

/// <summary>
/// Fetches the app-scoped SDK JWT that <c>ZoomSDK.Initialize</c> needs, from an auth endpoint that
/// holds the SDK secret.
/// <para>
/// This mirrors how the RioConf Windows head authenticates: it never ships the SDK secret, it POSTs
/// to a small worker that mints a short-lived token. The Client ID is safe to ship; the secret is
/// not, because anything in the package can be extracted from it.
/// </para>
/// <para>
/// Note the two Zoom token flavours are not interchangeable: the native SDKs authenticate the *app*
/// with this app-scoped JWT, whereas the Web SDK needs a meeting-scoped signature carrying mn/role.
/// </para>
/// </summary>
public sealed class ZoomAuthClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>
    /// App-scoped SDK JWT. Served by <c>POST {endpoint}/sdk-token</c>.
    /// </summary>
    public static async Task<string> GetSdkTokenAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(AppSettings.ZOOM_AUTH_ENDPOINT))
            throw new InvalidOperationException("AppSettings.ZOOM_AUTH_ENDPOINT is not set.");

        var url = $"{AppSettings.ZOOM_AUTH_ENDPOINT.TrimEnd('/')}/sdk-token";

        using var response = await Http.PostAsJsonAsync(
            url, new SdkTokenRequest(), ZoomAuthJsonContext.Default.SdkTokenRequest, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"The Zoom auth endpoint rejected the SDK token request ({(int)response.StatusCode}): {body}");
        }

        var payload = await response.Content.ReadFromJsonAsync(
            ZoomAuthJsonContext.Default.SdkTokenResponse, cancellationToken);

        return string.IsNullOrEmpty(payload?.Token)
            ? throw new InvalidOperationException("The Zoom auth endpoint returned an empty SDK token.")
            : payload.Token;
    }

    /// <summary>
    /// ZAK, required to *host* a meeting. Android takes it on
    /// <c>StartMeetingParamsWithoutLogin.ZoomAccessToken</c>; note Android's
    /// <c>JoinMeetingParams</c> has no ZAK field at all, so an anonymous join never needs one
    /// (unlike the Windows SDK, which accepts a userZAK on join as well).
    /// Best-effort: returns null rather than throwing, so a join is never blocked by it.
    /// </summary>
    public static async Task<string?> TryGetZakAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(AppSettings.ZOOM_AUTH_ENDPOINT))
            return null;

        try
        {
            var url = $"{AppSettings.ZOOM_AUTH_ENDPOINT.TrimEnd('/')}/zak";

            using var response = await Http.PostAsJsonAsync(
                url, new ZakRequest(AppSettings.ZOOM_USER_ID), ZoomAuthJsonContext.Default.ZakRequest, cancellationToken);

            if (!response.IsSuccessStatusCode) return null;

            var payload = await response.Content.ReadFromJsonAsync(
                ZoomAuthJsonContext.Default.ZakResponse, cancellationToken);

            return string.IsNullOrEmpty(payload?.Zak) ? null : payload.Zak;
        }
        catch
        {
            return null;
        }
    }
}

public sealed record SdkTokenRequest;

public sealed record SdkTokenResponse(
    [property: JsonPropertyName("token")] string? Token,
    [property: JsonPropertyName("sdkKey")] string? SdkKey);

public sealed record ZakRequest([property: JsonPropertyName("userId")] string UserId);

public sealed record ZakResponse([property: JsonPropertyName("zak")] string? Zak);

[JsonSerializable(typeof(SdkTokenRequest))]
[JsonSerializable(typeof(SdkTokenResponse))]
[JsonSerializable(typeof(ZakRequest))]
[JsonSerializable(typeof(ZakResponse))]
internal sealed partial class ZoomAuthJsonContext : JsonSerializerContext;

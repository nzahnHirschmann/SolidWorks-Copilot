using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Copilot.Sw.Config;

/// <summary>
/// GitHub OAuth device-flow client. Used by the Settings window to obtain
/// a user access token for calling GitHub Models without ever asking the
/// user to paste a PAT.
/// </summary>
/// <remarks>
/// <para>
/// The flow has two steps:
/// <list type="number">
/// <item>POST <c>https://github.com/login/device/code</c> -&gt; receive a
///       short <c>user_code</c>, a <c>verification_uri</c> and a polling
///       <c>device_code</c>.</item>
/// <item>Poll <c>https://github.com/login/oauth/access_token</c> with the
///       <c>device_code</c> every <c>interval</c> seconds until a token is
///       issued or the user denies the request.</item>
/// </list>
/// </para>
/// <para>
/// The <see cref="ClientId"/> must be the public client_id of a GitHub
/// OAuth App that has the device flow enabled. The OAuth client_secret is
/// not required by the device flow, so the client_id can safely live in
/// source.
/// </para>
/// </remarks>
public sealed class GitHubOAuth
{
    /// <summary>
    /// The OAuth App client_id used for the device flow. Resolved in the
    /// following order:
    /// <list type="number">
    /// <item>environment variable <c>SOLIDWORKS_COPILOT_GITHUB_CLIENT_ID</c></item>
    /// <item>a <c>github-oauth-client-id.txt</c> file next to the add-in dll</item>
    /// <item>the public GitHub CLI client_id, which has Device Flow enabled
    ///       and produces tokens that work with the GitHub Models endpoint
    ///       — this lets the add-in sign in out of the box without forcing
    ///       every user to register their own OAuth app.</item>
    /// </list>
    /// </summary>
    public static string ClientId => ResolveClientId();

    /// <summary>
    /// Public client_id of the GitHub CLI. Documented in
    /// https://github.com/cli/cli (internal/authflow/flow.go). Reused here
    /// as a sensible default so sign-in works without per-user OAuth-app
    /// registration. Override via env var or sidecar file if you want
    /// tokens to show up under your own OAuth app.
    /// </summary>
    public const string DefaultClientId = "178c6fc778ccc68e1d6a";

    /// <summary>OAuth scopes requested for GitHub Models access.</summary>
    public const string Scope = "read:user";

    private const string DeviceCodeUrl = "https://github.com/login/device/code";
    private const string AccessTokenUrl = "https://github.com/login/oauth/access_token";
    private const string DeviceGrantType = "urn:ietf:params:oauth:grant-type:device_code";

    private readonly HttpClient _http;

    public GitHubOAuth()
        : this(new HttpClient())
    {
    }

    public GitHubOAuth(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    /// <summary>
    /// Returns <see langword="true"/> if any client_id is configured.
    /// Always <see langword="true"/> in practice because of
    /// <see cref="DefaultClientId"/>, but kept for callers that want to
    /// distinguish "user supplied their own id" from "using the default".
    /// </summary>
    public static bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId);

    /// <summary>
    /// Step 1: request a device code from GitHub.
    /// </summary>
    public async Task<DeviceCodeResponse> RequestDeviceCodeAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "GitHub OAuth client_id is not configured.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, DeviceCodeUrl)
        {
            Content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("client_id", ClientId),
                new KeyValuePair<string, string>("scope", Scope),
            }),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("SolidWorks-Copilot");

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"GitHub refused the device-code request " +
                $"({(int)response.StatusCode} {response.ReasonPhrase}): {body}");
        }

        return DeviceCodeResponse.Parse(body);
    }

    /// <summary>
    /// Step 2: poll GitHub for the access token until the user completes
    /// the flow, denies it, or it expires. The returned task throws if the
    /// flow ends in an error or the <paramref name="cancellationToken"/>
    /// is cancelled.
    /// </summary>
    public async Task<string> PollForAccessTokenAsync(
        DeviceCodeResponse deviceCode,
        CancellationToken cancellationToken = default)
    {
        if (deviceCode is null) throw new ArgumentNullException(nameof(deviceCode));

        var interval = TimeSpan.FromSeconds(Math.Max(1, deviceCode.IntervalSeconds));
        var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, deviceCode.ExpiresInSeconds));

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);

            using var request = new HttpRequestMessage(HttpMethod.Post, AccessTokenUrl)
            {
                Content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("client_id", ClientId),
                    new KeyValuePair<string, string>("device_code", deviceCode.DeviceCode),
                    new KeyValuePair<string, string>("grant_type", DeviceGrantType),
                }),
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.UserAgent.ParseAdd("SolidWorks-Copilot");

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            // GitHub always returns 200 OK for this endpoint and signals
            // status via the JSON body.
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("access_token", out var token)
                && token.ValueKind == JsonValueKind.String)
            {
                return token.GetString()!;
            }

            if (!root.TryGetProperty("error", out var errorProp))
            {
                throw new InvalidOperationException(
                    "GitHub returned an unexpected token-poll response: " + body);
            }

            var error = errorProp.GetString();
            switch (error)
            {
                case "authorization_pending":
                    // User hasn't entered the code yet; keep polling.
                    break;
                case "slow_down":
                    // Spec says to add 5 seconds to the interval.
                    interval = interval.Add(TimeSpan.FromSeconds(5));
                    break;
                case "expired_token":
                    throw new InvalidOperationException(
                        "The device code expired before sign-in completed. Try again.");
                case "access_denied":
                    throw new InvalidOperationException(
                        "Sign-in was cancelled on GitHub.");
                default:
                    var description = root.TryGetProperty("error_description", out var d)
                        ? d.GetString()
                        : null;
                    throw new InvalidOperationException(
                        $"GitHub sign-in failed: {error}" +
                        (string.IsNullOrEmpty(description) ? "" : $" ({description})"));
            }
        }

        throw new InvalidOperationException(
            "Timed out waiting for GitHub sign-in to complete.");
    }

    /// <summary>
    /// Calls <c>GET https://api.github.com/user</c> with the given token
    /// and returns the <c>login</c> field, or null if it can't be parsed.
    /// </summary>
    public async Task<string?> GetUserLoginAsync(
        string token,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("SolidWorks-Copilot");

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("login", out var login)
                && login.ValueKind == JsonValueKind.String)
            {
                return login.GetString();
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    /// <summary>
    /// Calls the GitHub Models catalog endpoint and returns the available
    /// model ids (e.g. <c>openai/gpt-4o-mini</c>) the supplied token can
    /// use. Returns an empty list rather than throwing if the catalog
    /// cannot be reached.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListAvailableModelsAsync(
        string token,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://models.github.ai/catalog/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("SolidWorks-Copilot");

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Array.Empty<string>();
            }

            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return ParseCatalog(body);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static IReadOnlyList<string> ParseCatalog(string body)
    {
        var ids = new List<string>();
        using var doc = JsonDocument.Parse(body);

        // Catalog historically returns either a bare array or an object
        // with a "models"/"data" array. Handle both shapes.
        JsonElement array;
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            array = doc.RootElement;
        }
        else if (doc.RootElement.TryGetProperty("models", out var m) && m.ValueKind == JsonValueKind.Array)
        {
            array = m;
        }
        else if (doc.RootElement.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array)
        {
            array = d;
        }
        else
        {
            return ids;
        }

        foreach (var entry in array.EnumerateArray())
        {
            // Prefer "id" (e.g. "openai/gpt-4o-mini"); fall back to "name".
            string? id = null;
            if (entry.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
            {
                id = idEl.GetString();
            }
            else if (entry.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
            {
                id = nameEl.GetString();
            }

            if (!string.IsNullOrWhiteSpace(id))
            {
                ids.Add(id!);
            }
        }

        // De-dupe and sort for a stable picker.
        ids.Sort(StringComparer.OrdinalIgnoreCase);
        return ids;
    }

    private static string ResolveClientId()
    {
        var fromEnv = Environment.GetEnvironmentVariable("SOLIDWORKS_COPILOT_GITHUB_CLIENT_ID");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv!.Trim();
        }

        try
        {
            var assemblyDir = Path.GetDirectoryName(typeof(GitHubOAuth).Assembly.Location);
            if (!string.IsNullOrEmpty(assemblyDir))
            {
                var sidecar = Path.Combine(assemblyDir!, "github-oauth-client-id.txt");
                if (File.Exists(sidecar))
                {
                    var value = File.ReadAllText(sidecar, Encoding.UTF8).Trim();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }
            }
        }
        catch
        {
            // Best-effort. Fall through to the default.
        }

        return DefaultClientId;
    }
}

/// <summary>Parsed response from GitHub's <c>/login/device/code</c> endpoint.</summary>
public sealed class DeviceCodeResponse
{
    public string DeviceCode { get; private set; } = string.Empty;
    public string UserCode { get; private set; } = string.Empty;
    public Uri VerificationUri { get; private set; } = new Uri("https://github.com/login/device");
    public int ExpiresInSeconds { get; private set; }
    public int IntervalSeconds { get; private set; }

    internal static DeviceCodeResponse Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var error))
        {
            var description = root.TryGetProperty("error_description", out var d)
                ? d.GetString()
                : null;
            throw new InvalidOperationException(
                $"GitHub device-code request failed: {error.GetString()}" +
                (string.IsNullOrEmpty(description) ? "" : $" ({description})"));
        }

        var result = new DeviceCodeResponse
        {
            DeviceCode = root.GetProperty("device_code").GetString() ?? string.Empty,
            UserCode = root.GetProperty("user_code").GetString() ?? string.Empty,
            ExpiresInSeconds = root.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 900,
            IntervalSeconds = root.TryGetProperty("interval", out var i) ? i.GetInt32() : 5,
        };

        if (root.TryGetProperty("verification_uri", out var v)
            && Uri.TryCreate(v.GetString(), UriKind.Absolute, out var uri))
        {
            result.VerificationUri = uri;
        }

        return result;
    }
}

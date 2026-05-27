using Microsoft.SemanticKernel.AI.TextCompletion;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Copilot.Sw.Config;

/// <summary>
/// <see cref="ITextCompletion"/> adapter that talks to the GitHub Models
/// OpenAI-compatible <c>/chat/completions</c> endpoint, so the existing
/// semantic-function skills (which target text completion) can be served
/// by GitHub-hosted chat models such as <c>openai/gpt-4o-mini</c>.
/// </summary>
/// <remarks>
/// The prompt produced by Semantic Kernel for a semantic function is sent
/// as a single user message. The first choice's assistant content is
/// returned as the completion text.
/// </remarks>
internal sealed class GitHubModelsTextCompletion : ITextCompletion
{
    /// <summary>Default GitHub Models inference endpoint.</summary>
    public const string DefaultEndpoint = "https://models.github.ai/inference";

    // Shared HttpClient to avoid socket exhaustion. The base address is
    // baked into the request URI so we don't need a per-instance client.
    private static readonly HttpClient s_httpClient = new HttpClient();

    private readonly string _endpoint;
    private readonly string _model;
    private readonly string _apiKey;

    public GitHubModelsTextCompletion(string endpoint, string model, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException(
                "A GitHub Models model id is required (e.g. \"openai/gpt-4o-mini\").",
                nameof(model));
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException(
                "A GitHub token with the \"models:read\" scope is required.",
                nameof(apiKey));
        }

        _endpoint = (string.IsNullOrWhiteSpace(endpoint) ? DefaultEndpoint : endpoint!).TrimEnd('/');
        _model = model;
        _apiKey = apiKey;
    }

    public async Task<string> CompleteAsync(
        string text,
        CompleteRequestSettings requestSettings,
        CancellationToken cancellationToken = default)
    {
        if (requestSettings is null)
        {
            throw new ArgumentNullException(nameof(requestSettings));
        }

        var payload = new Dictionary<string, object?>
        {
            ["model"] = _model,
            ["messages"] = new[]
            {
                new { role = "user", content = text ?? string.Empty },
            },
            ["temperature"] = requestSettings.Temperature,
            ["top_p"] = requestSettings.TopP,
            ["presence_penalty"] = requestSettings.PresencePenalty,
            ["frequency_penalty"] = requestSettings.FrequencyPenalty,
        };

        if (requestSettings.MaxTokens > 0)
        {
            payload["max_tokens"] = requestSettings.MaxTokens;
        }

        if (requestSettings.StopSequences is { Count: > 0 } stop)
        {
            payload["stop"] = stop;
        }

        var json = JsonSerializer.Serialize(payload);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            _endpoint + "/chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await s_httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        var body = await response.Content
            .ReadAsStringAsync()
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"GitHub Models request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {body}");
        }

        return ExtractFirstChoiceContent(body);
    }

    private static string ExtractFirstChoiceContent(string body)
    {
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
        {
            return string.Empty;
        }

        var first = choices[0];
        if (first.TryGetProperty("message", out var message)
            && message.TryGetProperty("content", out var content)
            && content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? string.Empty;
        }

        // Some providers return "text" for legacy completion-style payloads.
        if (first.TryGetProperty("text", out var textProp)
            && textProp.ValueKind == JsonValueKind.String)
        {
            return textProp.GetString() ?? string.Empty;
        }

        return string.Empty;
    }
}

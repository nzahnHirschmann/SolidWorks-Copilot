namespace Copilot.Sw.Config;

/// <summary>
/// Provider kind. Currently only GitHub Models is supported; the enum is
/// retained so older settings files (which serialise this field) keep
/// deserialising.
/// </summary>
public enum ServerType
{
    /// <summary>
    /// GitHub Models (https://models.github.ai/inference).
    /// Authentication uses a GitHub OAuth token obtained via the device
    /// flow (see <see cref="GitHubOAuth"/>) and is stored in
    /// <see cref="TextCompletionConfig.Apikey"/>.
    /// </summary>
    GitHubModels = 2,
}

public sealed class TextCompletionConfig
{
    /// <summary>Default GitHub Models inference endpoint.</summary>
    public const string GitHubModelsDefaultEndpoint = "https://models.github.ai/inference/";

    /// <summary>Friendly name for the saved connection.</summary>
    public string? Name { get; set; }

    /// <summary>Provider kind. Always <see cref="ServerType.GitHubModels"/>.</summary>
    public ServerType Type { get; set; } = ServerType.GitHubModels;

    /// <summary>GitHub Models model id, e.g. <c>openai/gpt-4o-mini</c>.</summary>
    public string? Model { get; set; }

    /// <summary>Inference endpoint base URL. Defaults to <see cref="GitHubModelsDefaultEndpoint"/>.</summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// GitHub OAuth token (encrypted at rest via DPAPI). The plaintext
    /// value is supplied to the OpenAI connector as the bearer token.
    /// </summary>
    public string? Apikey { get; set; }

    /// <summary>
    /// Marks this entry as the kernel's default chat-completion service.
    /// </summary>
    public bool IsDefault { get; set; }
}

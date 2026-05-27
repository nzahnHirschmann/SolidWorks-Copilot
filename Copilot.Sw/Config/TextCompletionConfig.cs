namespace Copilot.Sw.Config;

public enum ServerType
{
    OpenAI,
    Azure,

    /// <summary>
    /// GitHub Models (https://models.github.ai/inference).
    /// Use a GitHub Personal Access Token with the "models:read" scope
    /// as <see cref="TextCompletionConfig.Apikey"/>. The
    /// <see cref="TextCompletionConfig.Model"/> field expects a GitHub
    /// Models identifier such as "openai/gpt-4o-mini" or "openai/gpt-4o".
    /// </summary>
    GitHubModels,
}

public sealed class TextCompletionConfig
{
    /// <summary>
    /// name for this config
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// type openai of azure
    /// </summary>
    public ServerType Type { get; set; }

    /// <summary>
    /// the llm model
    /// </summary>
    public string? Model { get; set; }

    /// <summary>
    /// endpoint if using azure
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// the api key for openai or azure
    /// </summary>
    public string? Apikey { get; set; }

    /// <summary>
    /// org,optional
    /// </summary>
    public string? Org { get; set; }

    /// <summary>
    /// When multiple configs are present, the one flagged as default is
    /// used as Semantic Kernel's default text-completion service.
    /// </summary>
    public bool IsDefault { get; set; }
}

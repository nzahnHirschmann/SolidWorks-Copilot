using Copilot.Sw.Config;
using Microsoft.SemanticKernel;
using System.Collections.Generic;
using System.Linq;

namespace Copilot.Sw.Extensions;

public static class KernelExtensions
{
    public static bool LoadConfigs(
        this KernelConfig kernelConfig, 
        IReadOnlyList<TextCompletionConfig> configs)
    {
        if (configs?.Any() != true)
        {
            return false;
        }

        kernelConfig.RemoveAllTextCompletionServices();
        kernelConfig.RemoveAllTextEmbeddingGenerationServices();
        foreach (var config in configs)
        {
            if (config.Type == ServerType.OpenAI)
            {
                kernelConfig.AddOpenAITextCompletionService(
                    config.Name,                       // alias used in the prompt templates' config.json
                    config.Model,                     // OpenAI Model Name
                    config.Apikey,            // OpenAI API key
                    config.Org
                    );
                kernelConfig.AddOpenAITextEmbeddingGenerationService(
                    config.Name,
                    "text-embedding-ada-002",
                    config.Apikey,
                    config.Org
                    );
            }
            else if (config.Type == ServerType.Azure)
            {
                kernelConfig.AddAzureTextCompletionService(
                    config.Name,
                    config.Model,
                    config.Apikey,
                    config.Org
                    );
            }
            else if (config.Type == ServerType.GitHubModels)
            {
                // GitHub Models exposes an OpenAI-compatible chat-completions
                // endpoint. We adapt it to ITextCompletion so the existing
                // semantic-function skills work unchanged.
                var endpoint = string.IsNullOrWhiteSpace(config.Endpoint)
                    ? GitHubModelsTextCompletion.DefaultEndpoint
                    : config.Endpoint!;

                kernelConfig.AddTextCompletionService(
                    config.Name,
                    _ => new GitHubModelsTextCompletion(endpoint, config.Model!, config.Apikey!));
            }
        }
        kernelConfig.SetDefaultTextCompletionService(configs.First().Name);

        return true;
    }
}

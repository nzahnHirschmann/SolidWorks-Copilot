using Copilot.Sw.Config;
using Microsoft.SemanticKernel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;

namespace Copilot.Sw.Extensions;

public static class KernelExtensions
{
    /// <summary>
    /// Registers a chat-completion service for every <paramref name="configs"/>
    /// entry on the given <paramref name="builder"/>. The provider marked
    /// <c>IsDefault</c> (or the first entry if none is) is registered without
    /// a serviceId so it becomes the kernel's default chat service.
    /// </summary>
    /// <returns><see langword="false"/> if <paramref name="configs"/> is null
    /// or empty.</returns>
    public static bool LoadConfigs(
        this IKernelBuilder builder,
        IReadOnlyList<TextCompletionConfig>? configs)
    {
        if (configs is null || configs.Count == 0)
        {
            return false;
        }

        var defaultConfig = configs.FirstOrDefault(c => c.IsDefault) ?? configs[0];

        foreach (var config in configs)
        {
            var isDefault = ReferenceEquals(config, defaultConfig);
            // Register the default config without a serviceId so it wins
            // GetRequiredService<IChatCompletionService>(); register the
            // others under their own name so callers can still pick them
            // explicitly via PromptExecutionSettings.ServiceId.
            var serviceId = isDefault ? null : config.Name;

            // GitHub Models exposes an OpenAI-compatible chat endpoint.
            // Point the OpenAI connector at it via a BaseAddress on a
            // dedicated HttpClient.
            var endpoint = string.IsNullOrWhiteSpace(config.Endpoint)
                ? TextCompletionConfig.GitHubModelsDefaultEndpoint
                : config.Endpoint!;
            if (!endpoint.EndsWith("/", StringComparison.Ordinal))
            {
                endpoint += "/";
            }

            var httpClient = new HttpClient
            {
                BaseAddress = new Uri(endpoint),
            };

            builder.AddOpenAIChatCompletion(
                modelId: config.Model!,
                apiKey: config.Apikey!,
                orgId: null,
                serviceId: serviceId,
                httpClient: httpClient);
        }

        return true;
    }
}

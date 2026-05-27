using Copilot.Sw.Config;
using Copilot.Sw.Skills;
using Microsoft.SemanticKernel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;

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

    /// <summary>
    /// Discovers every concrete <see cref="SldWorksSkillContext"/> subclass
    /// in the Copilot.Sw assembly that exposes at least one
    /// <c>[KernelFunction]</c> method, instantiates it, and adds it as a
    /// plugin on the kernel. Idempotent — plugins whose name is already
    /// registered are skipped.
    /// </summary>
    /// <returns>The names of the plugins that were added (or were already
    /// present) on this call.</returns>
    public static IReadOnlyList<string> AddAllNativeSkills(this Kernel kernel)
    {
        if (kernel is null)
        {
            throw new ArgumentNullException(nameof(kernel));
        }

        // Install the tool-call trace filter exactly once per kernel so
        // every native KernelFunction invocation is captured for the UI.
        // Conversation.ChatAsync opens an AsyncLocal capture scope per
        // turn; outside that scope the filter is a no-op, so this is safe
        // for every other code path (tests, planner, etc.).
        if (!kernel.FunctionInvocationFilters.Any(f => f is ToolCallTraceFilter))
        {
            kernel.FunctionInvocationFilters.Add(new ToolCallTraceFilter());
        }
        if (!kernel.FunctionInvocationFilters.Any(f => f is GovernanceFilter))
        {
            kernel.FunctionInvocationFilters.Add(new GovernanceFilter());
        }

        var assembly = typeof(SldWorksSkillContext).Assembly;
        var skillTypes = assembly
            .GetTypes()
            .Where(t => t.IsClass
                && !t.IsAbstract
                && typeof(SldWorksSkillContext).IsAssignableFrom(t)
                && t != typeof(SldWorksSkillContext))
            .Where(HasKernelFunction)
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToArray();

        var loaded = new List<string>(skillTypes.Length);
        foreach (var type in skillTypes)
        {
            var pluginName = type.Name;
            if (kernel.Plugins.Contains(pluginName))
            {
                loaded.Add(pluginName);
                continue;
            }

            try
            {
                var instance = Activator.CreateInstance(type);
                if (instance is null)
                {
                    continue;
                }
                kernel.Plugins.AddFromObject(instance, pluginName);
                loaded.Add(pluginName);
            }
            catch (Exception)
            {
                // A misbehaving skill must not take the chat pane down.
                // (Telemetry hook will land in P6.)
            }
        }
        return loaded;
    }

    private static bool HasKernelFunction(Type type)
    {
        return type
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Any(m => m.GetCustomAttribute<KernelFunctionAttribute>() is not null);
    }
}

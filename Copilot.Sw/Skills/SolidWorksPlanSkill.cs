using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Copilot.Sw.Skills;

/// <summary>
/// Drives a chat turn with auto function-calling enabled, so the model can
/// invoke any of the native SolidWorks skill plugins registered on the
/// <see cref="Kernel"/>. Replaces the legacy SK 0.13 SequentialPlanner flow.
/// </summary>
public sealed class SolidWorksPlanSkill
{
    private const string SystemPrompt =
        "You are an AI SolidWorks assistant. Your responses are professional " +
        "and concise. When the user asks you to perform an action that is " +
        "available as a function/tool, call the function instead of " +
        "describing it. Otherwise, answer in plain text.";

    private readonly Kernel _kernel;

    public SolidWorksPlanSkill(Kernel kernel)
    {
        _kernel = kernel;
    }

    /// <summary>
    /// Sends <paramref name="input"/> (plus <paramref name="history"/>) to the
    /// kernel's chat completion service with auto tool-calling enabled.
    /// </summary>
    /// <returns>The assistant's textual reply (may be empty if the model only
    /// invoked tools).</returns>
    public async Task<string> ChatAsync(
        string input,
        string history,
        CancellationToken cancellationToken)
    {
        var chat = _kernel.GetRequiredService<IChatCompletionService>();

        var messages = new ChatHistory();
        messages.AddSystemMessage(SystemPrompt);
        if (!string.IsNullOrWhiteSpace(history))
        {
            messages.AddSystemMessage("Conversation so far:\n" + history);
        }
        messages.AddUserMessage(input);

        var settings = new OpenAIPromptExecutionSettings
        {
            FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(),
            Temperature = 0.2,
        };

        var result = await chat
            .GetChatMessageContentAsync(messages, settings, _kernel, cancellationToken)
            .ConfigureAwait(false);

        return result.Content ?? string.Empty;
    }
}
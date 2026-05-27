using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
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

    private const string DryRunSystemPromptSuffix =
        "\n\nDRY-RUN MODE: do NOT call any functions/tools. Instead, " +
        "respond with a short numbered markdown plan listing the exact " +
        "function names (and key arguments) you would call, in order, " +
        "so the user can review before enabling execution.";

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

    /// <summary>
    /// Streaming variant of <see cref="ChatAsync"/>. Yields text chunks as
    /// they arrive from the model. Tool/function calls are still executed
    /// (auto), but their results are not echoed to the stream — only
    /// natural-language text chunks are surfaced.
    /// </summary>
    public async IAsyncEnumerable<string> ChatStreamingAsync(
        string input,
        string history,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var chunk in ChatStreamingAsync(input, history, dryRun: false, cancellationToken)
            .ConfigureAwait(false))
        {
            yield return chunk;
        }
    }

    /// <summary>
    /// Streaming variant of <see cref="ChatAsync"/>. Yields text chunks as
    /// they arrive from the model. When <paramref name="dryRun"/> is
    /// <see langword="true"/>, <see cref="FunctionChoiceBehavior.None"/> is
    /// used so the model advertises the tools but does not call them —
    /// instead it explains what it *would* do.
    /// </summary>
    public async IAsyncEnumerable<string> ChatStreamingAsync(
        string input,
        string history,
        bool dryRun,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var chat = _kernel.GetRequiredService<IChatCompletionService>();

        var messages = new ChatHistory();
        messages.AddSystemMessage(dryRun ? SystemPrompt + DryRunSystemPromptSuffix : SystemPrompt);
        if (!string.IsNullOrWhiteSpace(history))
        {
            messages.AddSystemMessage("Conversation so far:\n" + history);
        }
        messages.AddUserMessage(input);

        var settings = new OpenAIPromptExecutionSettings
        {
            FunctionChoiceBehavior = dryRun
                ? FunctionChoiceBehavior.None()
                : FunctionChoiceBehavior.Auto(),
            Temperature = 0.2,
        };

        await foreach (var chunk in chat
            .GetStreamingChatMessageContentsAsync(messages, settings, _kernel, cancellationToken)
            .ConfigureAwait(false))
        {
            if (!string.IsNullOrEmpty(chunk.Content))
            {
                yield return chunk.Content!;
            }
        }
    }
}
using CommunityToolkit.Mvvm.ComponentModel;
using Copilot.Sw.Extensions;
using Copilot.Sw.Skills;
using Copilot.Sw.Skills.SketchSkill;
using Copilot.Sw.Skills.SolidWorksSkill;
using Microsoft.SemanticKernel;
using System.Collections.ObjectModel;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Copilot.Sw.Models;

public class Conversation : ObservableObject
{
    private string _history = "";

    public ObservableCollection<Message> Messages { get; set; } = new();

    #region Chat
    public async Task ChatAsync(
        Kernel kernel,
        ISkillsProvider skillsProvider,
        string question,
        CancellationToken cancellationToken)
    {
        Messages.Add(Message.CreateAsk(question));

        EnsureNativePluginsLoaded(kernel);

        // Live-streamed answer message: stays in Messages and its Content
        // is appended to as chunks arrive (Message.Content is observable
        // so the UI updates incrementally).
        var streamed = new AnswerMessage { Content = string.Empty };
        Messages.Add(streamed);
        var buffer = new StringBuilder();

        try
        {
            var planSkill = new SolidWorksPlanSkill(kernel);
            await foreach (var chunk in planSkill
                .ChatStreamingAsync(question, _history, cancellationToken)
                .ConfigureAwait(true))
            {
                buffer.Append(chunk);
                streamed.Content = buffer.ToString();
            }

            var reply = buffer.ToString();
            AddHistory($"Me: {question}\nAI: {reply}\n");

            // If the final reply turned out to be a legacy XML plan, swap
            // the streamed AnswerMessage for an ActionAnswerMessage.
            if (SwPlanModel.TryParse(reply, out var planModel))
            {
                var index = Messages.IndexOf(streamed);
                if (index >= 0)
                {
                    Messages[index] = new ActionAnswerMessage(planModel);
                }
            }
        }
        catch (System.OperationCanceledException)
        {
            // User pressed Stop — keep whatever was already streamed.
        }
        catch (System.Exception)
        {
            // Drop the (possibly empty) streamed placeholder and let the
            // caller render an error (so it can also detect 401 / expired
            // tokens before deciding what to show).
            Messages.Remove(streamed);
            throw;
        }
    }

    public Task ChatWithContextAsync(
        Kernel kernel,
        ISkillsProvider skillsProvider,
        string question,
        CancellationToken cancellationToken)
        => ChatAsync(kernel, skillsProvider, question, cancellationToken);

    private static void EnsureNativePluginsLoaded(Kernel kernel)
    {
        // Idempotent: AddFromObject would throw if the plugin name is taken,
        // so only register what isn't there yet.
        if (!kernel.Plugins.Contains(nameof(DocumentCreationSkill)))
        {
            kernel.Plugins.AddFromObject(new DocumentCreationSkill(), nameof(DocumentCreationSkill));
        }
        if (!kernel.Plugins.Contains(nameof(SketchSegmentCreationSkill)))
        {
            kernel.Plugins.AddFromObject(
                new SketchSegmentCreationSkill(),
                nameof(SketchSegmentCreationSkill));
        }
    }
    #endregion

    #region Add
    internal void AddHistory(string theNewChatExchange)
    {
        _history += theNewChatExchange;
    }

    /// <summary>Reset the conversation: drop all messages and history.</summary>
    public void Clear()
    {
        Messages.Clear();
        _history = "";
    }
    #endregion
}

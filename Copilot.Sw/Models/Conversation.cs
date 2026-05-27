using CommunityToolkit.Mvvm.ComponentModel;
using Copilot.Sw.Extensions;
using Copilot.Sw.Skills;
using Copilot.Sw.Skills.SketchSkill;
using Copilot.Sw.Skills.SolidWorksSkill;
using Microsoft.SemanticKernel;
using System.Collections.ObjectModel;
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

        try
        {
            var planSkill = new SolidWorksPlanSkill(kernel);
            var reply = await planSkill
                .ChatAsync(question, _history, cancellationToken)
                .ConfigureAwait(false);

            AddHistory($"Me: {question}\nAI: {reply}\n");

            // If the plan came back as XML (legacy semantic-function output),
            // surface it as an action message; otherwise show plain text.
            if (SwPlanModel.TryParse(reply, out var planModel))
            {
                Messages.Add(new ActionAnswerMessage(planModel));
            }
            else
            {
                Messages.Add(Message.CreateAnswer(reply));
            }
        }
        catch (System.Exception ex)
        {
            Messages.Add(Message.CreateError(ex.Message));
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

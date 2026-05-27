using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using Copilot.Sw.Extensions;
using Copilot.Sw.Skills;
using Microsoft.SemanticKernel;
using SolidWorks.Interop.sldworks;
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

        kernel.AddAllNativeSkills();

        // Live-streamed answer message: stays in Messages and its Content
        // is appended to as chunks arrive (Message.Content is observable
        // so the UI updates incrementally).
        var streamed = new AnswerMessage { Content = string.Empty };
        Messages.Add(streamed);
        var buffer = new StringBuilder();

        // Wrap the whole turn in a SolidWorks undo group so the user can
        // revert every feature the model created in one Ctrl-Z.
        var undoDoc = GetActiveDoc();
        var undoLabel = $"Copilot: {Truncate(question, 60)}";
        var undoStarted = TryStartUndo(undoDoc, undoLabel);

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
        finally
        {
            if (undoStarted)
            {
                TryFinishUndo(undoDoc, undoLabel);
            }
        }
    }

    public Task ChatWithContextAsync(
        Kernel kernel,
        ISkillsProvider skillsProvider,
        string question,
        CancellationToken cancellationToken)
        => ChatAsync(kernel, skillsProvider, question, cancellationToken);

    private static IModelDoc2? GetActiveDoc()
    {
        try
        {
            return Ioc.Default.GetService<IAddin>()?.Sw?.IActiveDoc2;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryStartUndo(IModelDoc2? doc, string label)
    {
        if (doc is null)
        {
            return false;
        }
        try
        {
            doc.Extension.StartRecordingUndoObject();
            _ = label; // name is applied at finish-time
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryFinishUndo(IModelDoc2? doc, string label)
    {
        if (doc is null)
        {
            return;
        }
        try
        {
            doc.Extension.FinishRecordingUndoObject(label);
        }
        catch
        {
            // Best-effort; never let undo-grouping failures surface.
        }
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "…";
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

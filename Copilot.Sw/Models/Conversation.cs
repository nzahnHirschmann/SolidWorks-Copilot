using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using Copilot.Sw.Extensions;
using Copilot.Sw.Skills;
using Microsoft.SemanticKernel;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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
        => await ChatAsync(kernel, skillsProvider, question, dryRun: false, cancellationToken).ConfigureAwait(true);

    public async Task ChatAsync(
        Kernel kernel,
        ISkillsProvider skillsProvider,
        string question,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        // P5: preprocess slash commands and @-mentions before showing the
        // user message, so what lands in the transcript is the resolved
        // prompt the model actually receives.
        var resolved = ExpandAtMentions(ExpandSlashCommand(question));

        Messages.Add(Message.CreateAsk(resolved));

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
        var undoLabel = $"Copilot: {Truncate(resolved, 60)}";
        var undoStarted = TryStartUndo(undoDoc, undoLabel);

        // P5: capture every native KernelFunction invocation for this
        // turn into the streamed AnswerMessage.ToolCalls collection.
        var trace = ToolCallTraceFilter.BeginCapture();
        try
        {
            var planSkill = new SolidWorksPlanSkill(kernel);
            await foreach (var chunk in planSkill
                .ChatStreamingAsync(resolved, _history, dryRun, cancellationToken)
                .ConfigureAwait(true))
            {
                buffer.Append(chunk);
                streamed.Content = buffer.ToString();
            }

            // Move the captured calls onto the message (UI-observable).
            foreach (var entry in trace)
            {
                streamed.ToolCalls.Add(entry);
            }

            var reply = buffer.ToString();
            AddHistory($"Me: {resolved}\nAI: {reply}\n");

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
            // User pressed Stop — keep whatever was already streamed and
            // surface the partial tool-call trace.
            foreach (var entry in trace)
            {
                streamed.ToolCalls.Add(entry);
            }
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
            ToolCallTraceFilter.EndCapture();
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

    #region P5 prompt preprocessing
    /// <summary>
    /// Known slash commands and the canonical natural-language prompts
    /// they expand to. Keep entries terse and verb-led so the model picks
    /// the corresponding KernelFunction reliably.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> SlashCommands =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["/inspect-drawing"]   = "Call InspectDrawing and present the structured findings as a short markdown report (group by sheet, then severity).",
            ["/check-mates"]       = "Call EvaluateMateErrors and ListMates; summarise any mates with errors.",
            ["/mass-props"]        = "Call GetMassProperties on the active part / assembly and present mass, COG, volume, surface area in a small markdown table.",
            ["/context"]           = "Call GetActiveContext and summarise the active document, selection and any active sketch / feature in one paragraph.",
            ["/screenshot"]        = "Call Screenshot on the active document at 1024x768 and report the saved path.",
            ["/feature-tree"]      = "Call GetFeatureTree(maxDepth: 5) and present the top of the tree as a nested bullet list.",
            ["/bom"]               = "Call GetBoM(topLevelOnly: false) and present the result as a markdown table.",
            ["/new-part"]          = "Call CreatePart.",
            ["/new-assembly"]      = "Call CreateAssembly.",
            ["/new-drawing"]       = "Call CreateDrawingFromPart on the active part / assembly using the default template.",
            ["/rebuild"]           = "Call ForceRebuild.",
            ["/templates"]         = "Call ListTemplates and present the result verbatim so the user can pick one.",
            ["/template"]          = "Call GetTemplate with the supplied name, then execute the procedure it returns by invoking the referenced KernelFunctions in order.",
            ["/plan"]              = "Do NOT call any tools. Instead, respond with ONLY a single XML document of the form `<plan><skill skillname=\"FunctionName\" goal=\"what it does\"/>...</plan>` describing the exact sequence of KernelFunction calls you would make to satisfy the user's request. The UI will surface this plan for the user to review.",
            ["/help"]              = "List the available SolidWorks Copilot slash commands and what they do.",
        };

    /// <summary>
    /// If <paramref name="text"/> begins with a known slash command, return
    /// the canonical prompt; otherwise return the original text. The
    /// command may be followed by extra context which is appended.
    /// </summary>
    internal static string ExpandSlashCommand(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.TrimStart().StartsWith("/", StringComparison.Ordinal))
        {
            return text;
        }

        var trimmed = text.TrimStart();
        var spaceIdx = trimmed.IndexOf(' ');
        var command = spaceIdx > 0 ? trimmed.Substring(0, spaceIdx) : trimmed;
        var rest = spaceIdx > 0 ? trimmed.Substring(spaceIdx + 1).Trim() : string.Empty;

        if (string.Equals(command, "/help", StringComparison.OrdinalIgnoreCase))
        {
            var lines = SlashCommands
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => $"- `{kv.Key}` — {kv.Value}");
            return "Available slash commands:\n" + string.Join("\n", lines);
        }

        if (SlashCommands.TryGetValue(command, out var expansion))
        {
            return string.IsNullOrEmpty(rest)
                ? expansion
                : expansion + "\nAdditional context from the user: " + rest;
        }

        // Unknown command — leave as-is; the model will likely explain.
        return text;
    }

    /// <summary>
    /// Replaces @-mentions in <paramref name="text"/> with an inline
    /// context snapshot the model can act on. Supported tokens:
    /// <c>@active</c>, <c>@selection</c>, <c>@sheet</c>, <c>@components</c>,
    /// <c>@features</c>. Unknown tokens are left untouched.
    /// </summary>
    internal static string ExpandAtMentions(string text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('@') < 0)
        {
            return text;
        }

        var doc = GetActiveDoc();
        var contextBlocks = new List<string>();

        var pattern = new Regex(@"@(active|selection|sheet|components|features)\b", RegexOptions.IgnoreCase);
        var resolved = pattern.Replace(text, match =>
        {
            var token = match.Groups[1].Value.ToLowerInvariant();
            var snippet = TryResolveMention(token, doc);
            if (!string.IsNullOrEmpty(snippet))
            {
                contextBlocks.Add($"[{token}] {snippet}");
            }
            // Leave the visible token in place so the user can see what was referenced.
            return match.Value;
        });

        if (contextBlocks.Count == 0)
        {
            return resolved;
        }

        return resolved
            + "\n\nResolved context (do not echo verbatim, use as input):\n"
            + string.Join("\n", contextBlocks);
    }

    private static string? TryResolveMention(string token, IModelDoc2? doc)
    {
        if (doc is null) { return null; }
        try
        {
            switch (token)
            {
                case "active":
                    return $"{doc.GetPathName()} (type={((swDocumentTypes_e)doc.GetType())})";
                case "selection":
                    return SummariseSelection(doc);
                case "sheet" when doc is IDrawingDoc dwg:
                    return (dwg.GetCurrentSheet() as ISheet)?.GetName();
                case "components" when doc is IAssemblyDoc asm:
                    {
                        var comps = asm.GetComponents(true) as object[];
                        return comps is null
                            ? "0 components"
                            : $"{comps.Length} components: " + string.Join(", ",
                                comps.OfType<IComponent2>().Take(10).Select(c => c.Name2));
                    }
                case "features":
                    {
                        var feats = new List<string>();
                        var feat = doc.FirstFeature() as IFeature;
                        int safety = 0;
                        while (feat is not null && safety++ < 50)
                        {
                            feats.Add(feat.Name);
                            feat = feat.GetNextFeature() as IFeature;
                        }
                        return $"{feats.Count} top-level features: " + string.Join(", ", feats.Take(20));
                    }
            }
        }
        catch
        {
            // Best-effort context; never fail a chat turn over a mention.
        }
        return null;
    }

    private static string SummariseSelection(IModelDoc2 doc)
    {
        if (doc.SelectionManager is not ISelectionMgr selMgr) { return "no selection"; }
        var count = selMgr.GetSelectedObjectCount2(-1);
        if (count == 0) { return "no selection"; }
        var summaries = new List<string>(count);
        for (int i = 1; i <= count && i <= 5; i++)
        {
            try
            {
                var t = (swSelectType_e)selMgr.GetSelectedObjectType3(i, -1);
                summaries.Add(t.ToString().Replace("swSel", string.Empty));
            }
            catch { /* skip */ }
        }
        return $"{count} item(s): " + string.Join(", ", summaries);
    }
    #endregion
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

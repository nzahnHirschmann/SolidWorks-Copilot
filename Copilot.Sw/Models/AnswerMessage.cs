using System.Collections.ObjectModel;

namespace Copilot.Sw.Models;

public class AnswerMessage : Message
{
    public override MessageType MessageType => MessageType.Answer;

    /// <summary>
    /// Native KernelFunctions invoked by the model during the turn that
    /// produced this answer, in call order. Populated by
    /// <see cref="Copilot.Sw.Skills.ToolCallTraceFilter"/>.
    /// </summary>
    public ObservableCollection<ToolCallEntry> ToolCalls { get; } = new();
}
using System;

namespace Copilot.Sw.Skills;

/// <summary>
/// Marks a <c>[KernelFunction]</c> as requiring an explicit user
/// confirmation before the agent is allowed to invoke it. The active
/// <see cref="ConfirmationFilter"/> intercepts every such call and asks
/// the registered <see cref="IConfirmationPrompt"/> for a yes/no
/// decision. A "no" answer raises <see cref="OperationCanceledException"/>
/// which the trace filter records as a clean cancellation rather than a
/// failure.
///
/// Apply to skills whose effect is destructive or hard to reverse —
/// e.g. <c>DeleteBody</c>, <c>DeleteComponent</c>, <c>CloseActive</c>,
/// <c>SaveAs</c>, <c>SplitBody(consumeOriginal: true)</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class RequiresConfirmationAttribute : Attribute
{
    /// <summary>Short title shown to the user, e.g. "Delete component".</summary>
    public string Title { get; }

    /// <summary>
    /// Optional override of the message body. If null, the filter renders
    /// a default message of the form "Allow the agent to call {Plugin}.{Function}?"
    /// </summary>
    public string? Message { get; }

    public RequiresConfirmationAttribute(string title, string? message = null)
    {
        Title = string.IsNullOrWhiteSpace(title) ? "Confirm action" : title;
        Message = message;
    }
}

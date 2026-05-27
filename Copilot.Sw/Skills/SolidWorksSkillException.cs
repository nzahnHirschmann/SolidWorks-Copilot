using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Copilot.Sw.Skills;

/// <summary>
/// Normalised exception surfaced from every native KernelFunction so the
/// model — and the chat-pane error UI — never has to interpret raw COM
/// HRESULTs or framework <see cref="InvalidOperationException"/> stack
/// traces.
///
/// <para><see cref="Code"/> is a short stable identifier (e.g. <c>NO_DOC</c>,
/// <c>BAD_SELECTION</c>, <c>SW_COM</c>) that downstream tooling can
/// branch on. <see cref="Recoverable"/> hints whether the agent loop
/// should consider retrying or asking the user to adjust their request.</para>
/// </summary>
public sealed class SolidWorksSkillException : Exception
{
    public string Code { get; }
    public bool Recoverable { get; }

    public SolidWorksSkillException(string code, string message, bool recoverable = true, Exception? inner = null)
        : base(message, inner)
    {
        Code = string.IsNullOrEmpty(code) ? "SKILL" : code;
        Recoverable = recoverable;
    }

    /// <summary>
    /// Wrap an arbitrary exception thrown from inside a KernelFunction.
    /// Chooses a sensible <see cref="Code"/> from the runtime type and
    /// preserves the original message; the inner exception is kept so
    /// logs / traces can still reach the original stack.
    /// </summary>
    public static SolidWorksSkillException Wrap(Exception ex, string operation)
    {
        if (ex is SolidWorksSkillException already)
        {
            return already;
        }
        if (ex is OperationCanceledException)
        {
            // Caller cancellation is not an error condition we should
            // re-shape — preserve original semantics.
            throw ex;
        }

        var (code, recoverable) = ex switch
        {
            ArgumentException                  => ("BAD_ARG",       true),
            InvalidOperationException          => ("BAD_STATE",     true),
            NotSupportedException              => ("UNSUPPORTED",   false),
            FileNotFoundException              => ("NOT_FOUND",     true),
            UnauthorizedAccessException        => ("DENIED",        false),
            COMException com                   => (FormatComCode(com), true),
            _                                  => ("SKILL",         true),
        };

        return new SolidWorksSkillException(
            code,
            $"{operation}: {ex.Message}",
            recoverable,
            ex);
    }

    private static string FormatComCode(COMException ex)
        => $"SW_COM(0x{unchecked((uint)ex.HResult):X8})";
}

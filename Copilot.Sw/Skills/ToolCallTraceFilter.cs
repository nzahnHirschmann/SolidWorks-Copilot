using Copilot.Sw.Models;
using Microsoft.SemanticKernel;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Copilot.Sw.Skills;

/// <summary>
/// SK 1.x <see cref="IFunctionInvocationFilter"/> that records every
/// native KernelFunction call into an ambient (AsyncLocal) per-turn list.
/// The <see cref="Models.Conversation"/> opens a capture scope via
/// <see cref="BeginCapture"/> before each chat turn, then attaches the
/// resulting list to the streamed <see cref="AnswerMessage"/>.
///
/// Implementing this as an <c>AsyncLocal</c> rather than a constructor
/// argument means the same singleton filter can be installed once on the
/// <see cref="Kernel"/> and still produce per-turn traces without races
/// across concurrent chat panes.
/// </summary>
public sealed class ToolCallTraceFilter : IFunctionInvocationFilter
{
    private static readonly AsyncLocal<List<ToolCallEntry>?> _current = new();

    /// <summary>Start capturing into a fresh list scoped to the calling async flow.</summary>
    public static List<ToolCallEntry> BeginCapture()
    {
        var list = new List<ToolCallEntry>();
        _current.Value = list;
        return list;
    }

    /// <summary>End the current capture scope (does not clear the returned list).</summary>
    public static void EndCapture() => _current.Value = null;

    public async Task OnFunctionInvocationAsync(
        FunctionInvocationContext context,
        Func<FunctionInvocationContext, Task> next)
    {
        var sink = _current.Value;
        if (sink is null)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var entry = new ToolCallEntry
        {
            PluginName = context.Function.PluginName ?? string.Empty,
            FunctionName = context.Function.Name,
            ArgumentsSummary = SummariseArguments(context.Arguments),
            StartedAt = DateTimeOffset.Now,
        };
        sink.Add(entry);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await next(context).ConfigureAwait(false);
            stopwatch.Stop();
            entry.DurationMs = stopwatch.Elapsed.TotalMilliseconds;
            entry.Succeeded = true;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            entry.DurationMs = stopwatch.Elapsed.TotalMilliseconds;
            entry.Succeeded = false;
            entry.Error = ex.GetBaseException().Message;
            throw;
        }
    }

    private static string? SummariseArguments(KernelArguments? args)
    {
        if (args is null || args.Count == 0)
        {
            return null;
        }

        var parts = new List<string>(args.Count);
        foreach (var kv in args)
        {
            var value = kv.Value?.ToString() ?? "null";
            if (value.Length > 40)
            {
                value = value.Substring(0, 37) + "…";
            }
            parts.Add($"{kv.Key}={value}");
        }
        return string.Join(", ", parts);
    }
}

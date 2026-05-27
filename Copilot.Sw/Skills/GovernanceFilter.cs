using CommunityToolkit.Mvvm.DependencyInjection;
using Copilot.Sw.Models;
using Microsoft.SemanticKernel;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace Copilot.Sw.Skills;

/// <summary>
/// Abstraction over the UI prompt so tests / headless harnesses can
/// swap in a deterministic decision. Default implementation pops a WPF
/// <see cref="MessageBox"/> on the dispatcher thread.
/// </summary>
public interface IConfirmationPrompt
{
    bool Confirm(string title, string message);
}

internal sealed class WpfMessageBoxConfirmationPrompt : IConfirmationPrompt
{
    public bool Confirm(string title, string message)
    {
        var app = Application.Current;
        if (app?.Dispatcher is null)
        {
            // No UI loop (tests / xCAD register) — fall back to "deny"
            // so destructive actions can never happen silently.
            return false;
        }
        return app.Dispatcher.Invoke(() =>
            MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question)
                == MessageBoxResult.Yes);
    }
}

/// <summary>
/// SK 1.x <see cref="IFunctionInvocationFilter"/> implementing two P6
/// responsibilities at once:
///
/// <list type="bullet">
/// <item><description><b>Permissioned tools</b> — methods marked with
/// <see cref="RequiresConfirmationAttribute"/> are gated through
/// <see cref="IConfirmationPrompt"/>; a "no" raises
/// <see cref="OperationCanceledException"/>.</description></item>
/// <item><description><b>Error normalisation + telemetry</b> — every
/// exception thrown from a KernelFunction is wrapped via
/// <see cref="SolidWorksSkillException.Wrap"/>; every invocation (start
/// and end, with arguments and outcome) is appended as a single JSON
/// line to <c>%APPDATA%\Copilot.Sw\telemetry\YYYY-MM-DD.jsonl</c>.
/// </description></item>
/// </list>
///
/// Lives downstream of <see cref="ToolCallTraceFilter"/> (which captures
/// per-turn UI traces) so the trace filter still sees the normalised
/// exception type.
/// </summary>
public sealed class GovernanceFilter : IFunctionInvocationFilter
{
    private static readonly object _logLock = new();
    private static readonly string TelemetryDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Copilot.Sw", "telemetry");

    // Build once on first call; KernelFunctionMetadata doesn't surface
    // custom attributes, so we reflect by (plugin, function) name pair.
    private static readonly ConcurrentDictionary<string, RequiresConfirmationAttribute?> _confirmCache = new();

    private readonly IConfirmationPrompt _prompt;

    public GovernanceFilter() : this(Resolve()) { }

    public GovernanceFilter(IConfirmationPrompt prompt)
    {
        _prompt = prompt ?? throw new ArgumentNullException(nameof(prompt));
    }

    private static IConfirmationPrompt Resolve()
        => Ioc.Default.GetService<IConfirmationPrompt>() ?? new WpfMessageBoxConfirmationPrompt();

    public async Task OnFunctionInvocationAsync(
        FunctionInvocationContext context,
        Func<FunctionInvocationContext, Task> next)
    {
        var plugin = context.Function.PluginName ?? string.Empty;
        var name = context.Function.Name;
        var sw = Stopwatch.StartNew();

        // -------- Confirmation gate --------
        var gate = ResolveConfirmation(plugin, name);
        if (gate is not null)
        {
            var msg = gate.Message
                ?? $"The AI wants to call {plugin}.{name}({SummariseArgs(context.Arguments)}).\n\nAllow?";
            if (!_prompt.Confirm(gate.Title, msg))
            {
                AppendTelemetry(plugin, name, context.Arguments, 0,
                    "DENIED", "User denied confirmation prompt.");
                throw new OperationCanceledException(
                    $"User denied confirmation for {plugin}.{name}.");
            }
        }

        // -------- Invoke + normalise + log --------
        try
        {
            await next(context).ConfigureAwait(false);
            sw.Stop();
            AppendTelemetry(plugin, name, context.Arguments, sw.Elapsed.TotalMilliseconds,
                "OK", null);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            AppendTelemetry(plugin, name, context.Arguments, sw.Elapsed.TotalMilliseconds,
                "CANCELLED", null);
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            var wrapped = SolidWorksSkillException.Wrap(ex, $"{plugin}.{name}");
            AppendTelemetry(plugin, name, context.Arguments, sw.Elapsed.TotalMilliseconds,
                wrapped.Code, wrapped.Message);
            throw wrapped;
        }
    }

    private static RequiresConfirmationAttribute? ResolveConfirmation(string plugin, string function)
    {
        var key = plugin + "::" + function;
        return _confirmCache.GetOrAdd(key, _ => LookupAttribute(plugin, function));
    }

    private static RequiresConfirmationAttribute? LookupAttribute(string plugin, string function)
    {
        var asm = typeof(SldWorksSkillContext).Assembly;
        var type = asm.GetTypes().FirstOrDefault(t => t.Name == plugin);
        if (type is null) { return null; }
        var method = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m =>
            {
                var fn = m.GetCustomAttribute<KernelFunctionAttribute>();
                if (fn is null) { return false; }
                var n = fn.Name;
                if (string.IsNullOrEmpty(n)) { n = m.Name; }
                return string.Equals(n, function, StringComparison.Ordinal);
            });
        return method?.GetCustomAttribute<RequiresConfirmationAttribute>();
    }

    private static string SummariseArgs(KernelArguments? args)
    {
        if (args is null || args.Count == 0) { return string.Empty; }
        var sb = new StringBuilder();
        var first = true;
        foreach (var kv in args)
        {
            if (!first) { sb.Append(", "); }
            first = false;
            var v = kv.Value?.ToString() ?? "null";
            if (v.Length > 60) { v = v.Substring(0, 57) + "…"; }
            sb.Append(kv.Key).Append('=').Append(v);
        }
        return sb.ToString();
    }

    private static void AppendTelemetry(
        string plugin, string function, KernelArguments? args,
        double durationMs, string outcome, string? error)
    {
        try
        {
            Directory.CreateDirectory(TelemetryDir);
            var path = Path.Combine(TelemetryDir,
                DateTime.UtcNow.ToString("yyyy-MM-dd") + ".jsonl");
            var record = new
            {
                ts = DateTimeOffset.UtcNow.ToString("O"),
                plugin,
                function,
                args = args?.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString()),
                durationMs = Math.Round(durationMs, 1),
                outcome,
                error,
            };
            var line = JsonSerializer.Serialize(record) + Environment.NewLine;
            lock (_logLock)
            {
                File.AppendAllText(path, line);
            }
        }
        catch
        {
            // Telemetry must never break a chat turn.
        }
    }
}

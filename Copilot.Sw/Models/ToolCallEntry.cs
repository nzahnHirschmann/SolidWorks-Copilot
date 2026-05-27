using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace Copilot.Sw.Models;

/// <summary>
/// A single native KernelFunction invocation captured by
/// <see cref="Copilot.Sw.Skills.ToolCallTraceFilter"/> during a chat turn.
/// Surfaces in the assistant message so the user can see exactly which
/// SolidWorks skills ran, in what order, how long each took, and whether
/// any failed — the "Per-step status" half of the P5 plan UX.
/// </summary>
public sealed partial class ToolCallEntry : ObservableObject
{
    [ObservableProperty] private string _pluginName = string.Empty;
    [ObservableProperty] private string _functionName = string.Empty;
    [ObservableProperty] private string? _argumentsSummary;
    [ObservableProperty] private double _durationMs;
    [ObservableProperty] private bool _succeeded;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private DateTimeOffset _startedAt;

    public string Display
        => Succeeded
            ? $"✓ {FunctionName} ({DurationMs:F0} ms)"
            : $"✗ {FunctionName} — {Error}";
}

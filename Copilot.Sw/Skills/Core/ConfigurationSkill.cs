using Microsoft.SemanticKernel;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json;

namespace Copilot.Sw.Skills.Core;

/// <summary>
/// Configurations and equations: design variants and parametric
/// relationships.
/// </summary>
public sealed class ConfigurationSkill : SldWorksSkillContext
{
    [KernelFunction(nameof(AddConfiguration))]
    [Description("Add a new configuration to the active document. " +
        "Returns the configuration's name.")]
    public string AddConfiguration(string name, string? comment = null, bool suppressNewFeatures = false)
    {
        var doc = ActiveSwDoc
            ?? throw new InvalidOperationException("No active SolidWorks document.");
        int options = suppressNewFeatures
            ? (int)swConfigurationOptions2_e.swConfigOption_SuppressByDefault
            : 0;
        var ok = doc.AddConfiguration3(name, comment ?? string.Empty, string.Empty, options);
        if (ok is null)
        {
            throw new InvalidOperationException(
                $"AddConfiguration failed for '{name}'. Name may already be in use.");
        }
        return name;
    }

    [KernelFunction(nameof(ActivateConfiguration))]
    [Description("Activate the named configuration.")]
    public void ActivateConfiguration(string name)
    {
        var doc = ActiveSwDoc
            ?? throw new InvalidOperationException("No active SolidWorks document.");
        if (!doc.ShowConfiguration2(name))
        {
            throw new InvalidOperationException(
                $"Could not activate configuration '{name}'.");
        }
    }

    [KernelFunction(nameof(ListConfigurations))]
    [Description("List all configurations in the active document as JSON " +
        "with the active configuration flagged.")]
    public string ListConfigurations()
    {
        var doc = ActiveSwDoc
            ?? throw new InvalidOperationException("No active SolidWorks document.");
        var names = doc.GetConfigurationNames() as string[] ?? Array.Empty<string>();
        var active = (doc.GetActiveConfiguration() as IConfiguration)?.Name;
        var rows = new List<object>(names.Length);
        foreach (var n in names)
        {
            rows.Add(new { name = n, active = n == active });
        }
        return JsonSerializer.Serialize(rows);
    }

    [KernelFunction(nameof(AddEquation))]
    [Description("Add an equation to the active document. Use SolidWorks " +
        "equation syntax, e.g. '\"D1@Sketch1\" = \"D2@Sketch2\" * 2'.")]
    public int AddEquation(string equation)
    {
        if (string.IsNullOrWhiteSpace(equation))
        {
            throw new ArgumentException("Equation cannot be empty.", nameof(equation));
        }
        var doc = ActiveSwDoc
            ?? throw new InvalidOperationException("No active SolidWorks document.");
        var mgr = doc.GetEquationMgr()
            ?? throw new InvalidOperationException("No EquationMgr available on this document.");
        var index = mgr.Add3(-1, equation, true,
            (int)swInConfigurationOpts_e.swAllConfiguration,
            null);
        if (index < 0)
        {
            throw new InvalidOperationException(
                $"AddEquation failed for '{equation}'. Check the syntax " +
                "(quote dimension references like \"D1@Sketch1\").");
        }
        return index;
    }
}

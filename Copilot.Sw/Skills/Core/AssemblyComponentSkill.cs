using Microsoft.SemanticKernel;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text.Json;

namespace Copilot.Sw.Skills.Core;

/// <summary>
/// Assembly-level component management: insert, list, fix/float,
/// suppress/resolve. These are the building blocks the LLM needs before
/// it can mate or analyse an assembly.
/// </summary>
public sealed class AssemblyComponentSkill : SldWorksSkillContext
{
    [KernelFunction(nameof(InsertComponent))]
    [Description("Insert a part or sub-assembly into the active assembly " +
        "from disk. Coordinates are in the active document's length unit. " +
        "Returns the inserted component's name.")]
    public string InsertComponent(
        [Description("Absolute path to the .sldprt / .sldasm to insert.")] string path,
        double x = 0,
        double y = 0,
        double z = 0,
        [Description("Optional named configuration to instance.")] string? configurationName = null)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new FileNotFoundException("Component file not found.", path);
        }
        var asm = RequireAssembly();
        var doc = ActiveSwDoc!;
        var mx = SwUnits.ToMeters(x, null, doc);
        var my = SwUnits.ToMeters(y, null, doc);
        var mz = SwUnits.ToMeters(z, null, doc);

        Component2? comp;
        if (!string.IsNullOrWhiteSpace(configurationName))
        {
            comp = asm.AddComponent5(
                path,
                (int)swAddComponentConfigOptions_e.swAddComponentConfigOptions_CurrentSelectedConfig,
                string.Empty,
                false,
                configurationName!,
                mx, my, mz) as Component2;
        }
        else
        {
            comp = asm.AddComponent4(path, string.Empty, mx, my, mz) as Component2;
        }

        return comp?.Name2
            ?? throw new InvalidOperationException(
                $"SolidWorks refused to insert '{path}'. The file may not " +
                "be a valid component, or it may already be open in a " +
                "conflicting state.");
    }

    [KernelFunction(nameof(ListComponents))]
    [Description("List all components in the active assembly as JSON. " +
        "Each entry includes name, referenced configuration, suppression " +
        "state, and whether the component is fixed.")]
    public string ListComponents(
        [Description("If true (default), only top-level components are listed.")] bool topLevelOnly = true)
    {
        var asm = RequireAssembly();
        var rows = new List<object>();

        if (asm.GetComponents(topLevelOnly) is object[] comps)
        {
            foreach (var raw in comps)
            {
                if (raw is not IComponent2 c)
                {
                    continue;
                }
                rows.Add(new
                {
                    name = c.Name2,
                    config = c.ReferencedConfiguration,
                    isFixed = c.IsFixed(),
                    isSuppressed = c.IsSuppressed(),
                    isHidden = c.IsHidden(false),
                });
            }
        }
        return JsonSerializer.Serialize(rows);
    }

    [KernelFunction(nameof(FixSelectedComponent))]
    [Description("Fix the currently selected component(s) in the active " +
        "assembly (so they cannot move).")]
    public void FixSelectedComponent()
    {
        RequireAssembly().FixComponent();
    }

    [KernelFunction(nameof(FloatSelectedComponent))]
    [Description("Un-fix the currently selected component(s) in the " +
        "active assembly (so they can move again).")]
    public void FloatSelectedComponent()
    {
        RequireAssembly().UnfixComponent();
    }

    [KernelFunction(nameof(SuppressSelectedComponent))]
    [Description("Suppress the currently selected component(s) so they " +
        "are removed from the build and mass calculations.")]
    public void SuppressSelectedComponent()
    {
        RequireAssembly().SetComponentSuppression(
            (int)swComponentSuppressionState_e.swComponentSuppressed);
    }

    [KernelFunction(nameof(ResolveSelectedComponent))]
    [Description("Bring the currently selected suppressed/lightweight " +
        "component(s) back to a fully resolved state.")]
    public void ResolveSelectedComponent()
    {
        RequireAssembly().SetComponentSuppression(
            (int)swComponentSuppressionState_e.swComponentFullyResolved);
    }

    private IAssemblyDoc RequireAssembly()
    {
        var doc = ActiveSwDoc
            ?? throw new InvalidOperationException("No active SolidWorks document.");
        if (doc is not IAssemblyDoc asm
            || (swDocumentTypes_e)doc.GetType() != swDocumentTypes_e.swDocASSEMBLY)
        {
            throw new InvalidOperationException(
                "Assembly skills require an active assembly document.");
        }
        return asm;
    }
}

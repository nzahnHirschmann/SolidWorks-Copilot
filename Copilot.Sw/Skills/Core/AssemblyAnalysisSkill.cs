using Microsoft.SemanticKernel;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json;

namespace Copilot.Sw.Skills.Core;

/// <summary>
/// Read-only assembly QA: interference detection + rebuild. These are
/// half of the “check accuracy” headline for assembly documents — the
/// drawing-side checks live in P3.
/// </summary>
public sealed class AssemblyAnalysisSkill : SldWorksSkillContext
{
    [KernelFunction(nameof(RunInterferenceDetection))]
    [Description("Run interference detection on the active assembly and " +
        "return a JSON list of interfering component pairs with the " +
        "intersection volume in cubic metres. Empty list means no clashes.")]
    public string RunInterferenceDetection(
        [Description("Treat coincident (zero-volume) contacts as interferences.")] bool treatCoincidenceAsInterference = false,
        [Description("Include multibody-part self-interferences.")] bool includeMultibodyParts = true)
    {
        var asm = RequireAssembly();
        var mgr = asm.InterferenceDetectionManager
            ?? throw new InvalidOperationException(
                "SolidWorks did not return an InterferenceDetectionManager.");

        mgr.TreatCoincidenceAsInterference = treatCoincidenceAsInterference;
        mgr.IncludeMultibodyPartInterferences = includeMultibodyParts;
        mgr.TreatSubAssembliesAsComponents = true;
        mgr.IgnoreHiddenBodies = true;

        var results = new List<object>();
        try
        {
            if (mgr.GetInterferences() is object[] interferences)
            {
                foreach (var raw in interferences)
                {
                    if (raw is not IInterference inter)
                    {
                        continue;
                    }
                    var componentNames = new List<string>();
                    if (inter.Components is object[] comps)
                    {
                        foreach (var c in comps)
                        {
                            if (c is IComponent2 cc)
                            {
                                componentNames.Add(cc.Name2);
                            }
                        }
                    }
                    results.Add(new
                    {
                        components = componentNames,
                        volume_m3 = inter.Volume,
                        isFastener = inter.IsFastener,
                    });
                }
            }
        }
        finally
        {
            mgr.Done();
        }

        return JsonSerializer.Serialize(new
        {
            interferenceCount = results.Count,
            interferences = results,
        });
    }

    [KernelFunction(nameof(ForceRebuild))]
    [Description("Force-rebuild every feature in the active document " +
        "(Ctrl+Q). Useful after running a long plan to surface any " +
        "feature errors.")]
    public void ForceRebuild()
    {
        var doc = ActiveSwDoc
            ?? throw new InvalidOperationException("No active SolidWorks document.");
        doc.ForceRebuild3(false);
    }

    [KernelFunction(nameof(MeasureClearance))]
    [Description("Measure the clearance / distance between the two " +
        "currently selected entities (faces, edges, vertices, or " +
        "components). Returns JSON with distance, centerDistance, " +
        "normalDistance, deltaX/Y/Z (all in millimetres), plus " +
        "isParallel / isPerpendicular / isIntersecting flags.")]
    public string MeasureClearance()
    {
        var doc = ActiveSwDoc
            ?? throw new InvalidOperationException("No active SolidWorks document.");
        var measure = doc.Extension.CreateMeasure() as IMeasure
            ?? throw new InvalidOperationException("Could not create measure tool.");
        if (!measure.Calculate(null))
        {
            throw new InvalidOperationException(
                "MeasureClearance failed. Pre-select two entities (faces, " +
                "edges, vertices, or components) before calling.");
        }

        // Native units are metres; convert to mm for the LLM.
        const double m2mm = 1000.0;
        var result = new
        {
            distanceMm = measure.Distance * m2mm,
            centerDistanceMm = measure.CenterDistance * m2mm,
            normalDistanceMm = measure.NormalDistance * m2mm,
            deltaXMm = measure.DeltaX * m2mm,
            deltaYMm = measure.DeltaY * m2mm,
            deltaZMm = measure.DeltaZ * m2mm,
            isParallel = measure.IsParallel,
            isPerpendicular = measure.IsPerpendicular,
            isIntersecting = measure.IsIntersect,
        };
        return JsonSerializer.Serialize(result);
    }

    private IAssemblyDoc RequireAssembly()
    {
        var doc = ActiveSwDoc
            ?? throw new InvalidOperationException("No active SolidWorks document.");
        if (doc is not IAssemblyDoc asm
            || (swDocumentTypes_e)doc.GetType() != swDocumentTypes_e.swDocASSEMBLY)
        {
            throw new InvalidOperationException(
                "Interference detection requires an active assembly document.");
        }
        return asm;
    }
}

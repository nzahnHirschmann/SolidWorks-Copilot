using Microsoft.SemanticKernel;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.ComponentModel;

namespace Copilot.Sw.Skills.Core;

/// <summary>
/// Starts, exits, and re-enters sketches. Required by every sketch-entity
/// skill — <c>CreateCircle</c> et al. assume an active sketch but nothing
/// in the kernel previously gave the model a way to start one.
/// </summary>
public sealed class SketchLifecycleSkill : SldWorksSkillContext
{
    [KernelFunction(nameof(InsertSketchOnPlane))]
    [Description("Start a new sketch on a standard reference plane in the " +
        "active part. Returns the name of the created sketch. Valid plane " +
        "names: 'Front', 'Top', 'Right' (case-insensitive).")]
    public string InsertSketchOnPlane(
        [Description("Plane name: Front, Top, or Right.")] string plane)
    {
        var doc = RequirePart();
        var planeName = NormalizePlane(plane);

        // Select the reference plane, then ask SketchManager to insert.
        var selected = doc.Extension.SelectByID2(
            planeName,
            "PLANE",
            0, 0, 0,
            false,
            0,
            null,
            (int)swSelectOption_e.swSelectOptionDefault);
        if (!selected)
        {
            throw new InvalidOperationException(
                $"Could not select plane '{planeName}'. The active document " +
                "may not be a part, or the plane may have been renamed.");
        }

        doc.SketchManager.InsertSketch(true);
        doc.ClearSelection2(true);

        var sketch = doc.SketchManager.ActiveSketch as IFeature;
        return sketch?.Name ?? "Sketch";
    }

    [KernelFunction(nameof(ExitSketch))]
    [Description("Exit the active sketch (commits it). No-op if no sketch is open.")]
    public void ExitSketch()
    {
        var doc = ActiveSwDoc;
        if (doc?.SketchManager?.ActiveSketch is null)
        {
            return;
        }
        doc.SketchManager.InsertSketch(true);
    }

    [KernelFunction(nameof(EditSketch))]
    [Description("Re-enter an existing sketch by name (e.g. 'Sketch1') to add " +
        "more geometry to it.")]
    public void EditSketch(
        [Description("Sketch feature name as shown in the FeatureManager tree.")] string sketchName)
    {
        if (string.IsNullOrWhiteSpace(sketchName))
        {
            throw new ArgumentException("Sketch name is required.", nameof(sketchName));
        }
        var doc = RequireActiveDoc();

        var selected = doc.Extension.SelectByID2(
            sketchName,
            "SKETCH",
            0, 0, 0,
            false,
            0,
            null,
            (int)swSelectOption_e.swSelectOptionDefault);
        if (!selected)
        {
            throw new InvalidOperationException(
                $"Sketch '{sketchName}' not found in the active document.");
        }
        doc.EditSketch();
    }

    private static string NormalizePlane(string plane)
    {
        if (string.IsNullOrWhiteSpace(plane))
        {
            throw new ArgumentException("Plane is required.", nameof(plane));
        }
        return plane.Trim().ToLowerInvariant() switch
        {
            "front" or "frontplane" or "front plane" => "Front Plane",
            "top" or "topplane" or "top plane" => "Top Plane",
            "right" or "rightplane" or "right plane" => "Right Plane",
            _ => throw new ArgumentException(
                $"Unknown plane '{plane}'. Use Front, Top, or Right.", nameof(plane)),
        };
    }

    private IModelDoc2 RequireActiveDoc()
    {
        var doc = ActiveSwDoc;
        if (doc is null)
        {
            throw new InvalidOperationException("No active SolidWorks document.");
        }
        return doc;
    }

    private IModelDoc2 RequirePart()
    {
        var doc = RequireActiveDoc();
        var type = (swDocumentTypes_e)doc.GetType();
        if (type != swDocumentTypes_e.swDocPART)
        {
            throw new InvalidOperationException(
                "InsertSketchOnPlane requires an active part document.");
        }
        return doc;
    }
}

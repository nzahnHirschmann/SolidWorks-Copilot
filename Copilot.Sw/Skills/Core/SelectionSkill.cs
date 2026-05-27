using Microsoft.SemanticKernel;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.ComponentModel;

namespace Copilot.Sw.Skills.Core;

/// <summary>
/// Selection helpers. Most SolidWorks feature APIs read from the current
/// selection set, so the model needs explicit tools to manipulate it.
/// </summary>
public sealed class SelectionSkill : SldWorksSkillContext
{
    [KernelFunction(nameof(ClearSelection))]
    [Description("Clear the current SolidWorks selection.")]
    public void ClearSelection()
    {
        ActiveSwDoc?.ClearSelection2(true);
    }

    [KernelFunction(nameof(SelectByName))]
    [Description("Add an entity to the selection by name. " +
        "Common type codes: FACE, EDGE, VERTEX, PLANE, AXIS, SKETCH, " +
        "BODYFEATURE, REFERENCEPOINT, COMPONENT.")]
    public void SelectByName(
        [Description("The entity's name as shown in the FeatureManager tree.")] string name,
        [Description("SolidWorks selection type code (FACE, EDGE, PLANE, SKETCH, BODYFEATURE, ...).")] string type,
        [Description("If true, add to the existing selection instead of replacing it.")] bool append = false)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Name is required.", nameof(name));
        }
        if (string.IsNullOrWhiteSpace(type))
        {
            throw new ArgumentException("Type is required.", nameof(type));
        }
        var doc = RequireActiveDoc();

        var ok = doc.Extension.SelectByID2(
            name,
            type.Trim().ToUpperInvariant(),
            0, 0, 0,
            append,
            0,
            null,
            (int)swSelectOption_e.swSelectOptionDefault);
        if (!ok)
        {
            throw new InvalidOperationException(
                $"SelectByName failed for '{name}' ({type}). The entity may " +
                "not exist or the type code may be wrong.");
        }
    }

    [KernelFunction(nameof(SelectFaceAt))]
    [Description("Select the face nearest to the given xyz point (in the " +
        "active document's length unit). Useful before Fillet/Shell/Draft. " +
        "Pass append=true to keep previous selections.")]
    public void SelectFaceAt(
        double x,
        double y,
        double z,
        bool append = false)
    {
        var doc = RequireActiveDoc();
        var mx = SwUnits.ToMeters(x, null, doc);
        var my = SwUnits.ToMeters(y, null, doc);
        var mz = SwUnits.ToMeters(z, null, doc);

        var ok = doc.Extension.SelectByID2(
            string.Empty,
            "FACE",
            mx, my, mz,
            append,
            0,
            null,
            (int)swSelectOption_e.swSelectOptionDefault);
        if (!ok)
        {
            throw new InvalidOperationException(
                $"No face found at ({x},{y},{z}) {SwUnits.GetDocumentLengthUnit(doc)}.");
        }
    }

    [KernelFunction(nameof(GetSelectionCount))]
    [Description("Return the number of entities currently selected.")]
    public int GetSelectionCount()
    {
        var selMgr = ActiveSwDoc?.SelectionManager as ISelectionMgr;
        return selMgr?.GetSelectedObjectCount2(-1) ?? 0;
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
}

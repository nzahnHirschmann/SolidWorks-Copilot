using Microsoft.SemanticKernel;
using SolidWorks.Interop.sldworks;
using System;
using System.ComponentModel;

namespace Copilot.Sw.Skills.Core;

/// <summary>
/// Sketch constraints: relations between selected entities, and
/// dimensions placed at a screen-space point. Pre-select entities before
/// calling these methods.
/// </summary>
public sealed class SketchConstraintSkill : SldWorksSkillContext
{
    [KernelFunction(nameof(AddRelation))]
    [Description("Add a geometric relation between the currently selected " +
        "sketch entities. Supported: Horizontal, Vertical, Coincident, " +
        "Collinear, Concentric, Equal, Tangent, Perpendicular, Parallel, " +
        "Fix, Midpoint, Symmetric.")]
    public void AddRelation(string relationType)
    {
        var doc = ActiveSwDoc
            ?? throw new InvalidOperationException("No active SolidWorks document.");
        if (doc.SketchManager?.ActiveSketch is null)
        {
            throw new InvalidOperationException("AddRelation requires an active sketch.");
        }
        var key = relationType?.Trim().ToUpperInvariant() switch
        {
            "HORIZONTAL" => "sgHORIZONTAL2D",
            "VERTICAL" => "sgVERTICAL2D",
            "COINCIDENT" => "sgCOINCIDENT2D",
            "COLLINEAR" => "sgCOLLINEAR2D",
            "CONCENTRIC" => "sgCONCENTRIC2D",
            "EQUAL" => "sgEQUAL2D",
            "TANGENT" => "sgTANGENT2D",
            "PERPENDICULAR" => "sgPERPENDICULAR2D",
            "PARALLEL" => "sgPARALLEL2D",
            "FIX" or "FIXED" => "sgFIXED2D",
            "MIDPOINT" or "MIDDLE" => "sgATMIDDLE2D",
            "SYMMETRIC" => "sgSYMMETRIC2D",
            _ => throw new ArgumentException(
                $"Unknown relation '{relationType}'.", nameof(relationType)),
        };
        doc.SketchAddConstraints(key);
    }

    [KernelFunction(nameof(AddDimension))]
    [Description("Place a driving dimension at the given screen-space " +
        "location for the currently selected entity (or pair of " +
        "entities). value is in the document's length unit; pass null/0 " +
        "to keep the current geometry.")]
    public string AddDimension(double x, double y, double? value = null, string? unit = null)
    {
        var doc = ActiveSwDoc
            ?? throw new InvalidOperationException("No active SolidWorks document.");
        var mx = SwUnits.ToMeters(x, unit, doc);
        var my = SwUnits.ToMeters(y, unit, doc);
        var dim = doc.AddDimension2(mx, my, 0) as IDisplayDimension;
        if (dim is null)
        {
            throw new InvalidOperationException(
                "AddDimension failed. Select the entity (or pair) to " +
                "dimension before calling.");
        }
        var d = dim.GetDimension2(0);
        if (value.HasValue && value.Value != 0 && d is not null)
        {
            d.SetSystemValue3(
                SwUnits.ToMeters(value.Value, unit, doc),
                (int)SolidWorks.Interop.swconst.swInConfigurationOpts_e.swThisConfiguration,
                null);
        }
        return d?.FullName ?? string.Empty;
    }
}

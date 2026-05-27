using Microsoft.SemanticKernel;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.ComponentModel;

namespace Copilot.Sw.Skills.Core;

/// <summary>
/// Whole-body operations: move/copy, delete, thicken-surface.
/// </summary>
public sealed class BodyOperationSkill : SldWorksSkillContext
{
    [KernelFunction(nameof(MoveCopyBody))]
    [Description("Move and/or copy the currently selected body by a " +
        "translation vector. Lengths are in the document's length unit; " +
        "rotation angles in degrees. Set 'copy' true to leave the original.")]
    public string MoveCopyBody(
        double translateX = 0, double translateY = 0, double translateZ = 0,
        double rotateXDegrees = 0, double rotateYDegrees = 0, double rotateZDegrees = 0,
        bool copy = false,
        int numCopies = 1,
        string? unit = null)
    {
        var doc = ActiveSwDoc
            ?? throw new InvalidOperationException("No active SolidWorks document.");
        var feat = doc.FeatureManager.InsertMoveCopyBody2(
            TransX: SwUnits.ToMeters(translateX, unit, doc),
            TransY: SwUnits.ToMeters(translateY, unit, doc),
            TransZ: SwUnits.ToMeters(translateZ, unit, doc),
            TransDist: 0,
            RotPointX: 0, RotPointY: 0, RotPointZ: 0,
            RotAngleX: SwUnits.DegreesToRadians(rotateXDegrees),
            RotAngleY: SwUnits.DegreesToRadians(rotateYDegrees),
            RotAngleZ: SwUnits.DegreesToRadians(rotateZDegrees),
            BCopy: copy,
            NumCopies: Math.Max(1, numCopies)) as IFeature
            ?? throw new InvalidOperationException(
                "MoveCopyBody failed. Pre-select the body to move.");
        return feat.Name;
    }

    [KernelFunction(nameof(DeleteBody))]
    [Description("Delete the currently selected body/bodies (BodyDelete " +
        "feature).")]
    public string DeleteBody()
    {
        var doc = ActiveSwDoc
            ?? throw new InvalidOperationException("No active SolidWorks document.");
        var feat = doc.FeatureManager.InsertDeleteBody2(false) as IFeature
            ?? throw new InvalidOperationException(
                "DeleteBody failed. Pre-select the body to delete.");
        return feat.Name;
    }

    [KernelFunction(nameof(ThickenSurface))]
    [Description("Thicken the currently selected surface into a solid. " +
        "Direction: 0 = first side, 1 = second side, 2 = both sides.")]
    public string ThickenSurface(double thickness, int direction = 0, bool merge = true, string? unit = null)
    {
        var doc = ActiveSwDoc
            ?? throw new InvalidOperationException("No active SolidWorks document.");
        var t = SwUnits.ToMeters(thickness, unit, doc);
        var feat = doc.FeatureManager.FeatureBossThicken(
            Thickness: t,
            Direction: direction,
            FaceIndex: 0,
            FillVolume: false,
            Merge: merge,
            UseFeatScope: true,
            UseAutoSelect: true) as IFeature
            ?? throw new InvalidOperationException(
                "ThickenSurface failed. Pre-select the surface body.");
        return feat.Name;
    }
}

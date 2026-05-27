using Microsoft.SemanticKernel;
using SolidWorks.Interop.sldworks;
using System;
using System.ComponentModel;

namespace Copilot.Sw.Skills.Core;

/// <summary>
/// Sketch entities beyond the basics: slot, ellipse, sketch trim,
/// construction-geometry toggle, and sketch patterns.
/// </summary>
public sealed class AdvancedSketchSkill : SldWorksSkillContext
{
    [KernelFunction(nameof(CreateSlot))]
    [Description("Create a straight or arc slot in the active sketch. " +
        "type: 'StraightCenter', 'StraightThreePoint', 'CenterPointArc', " +
        "'ThreePointArc'. width is the slot width.")]
    public void CreateSlot(
        double x1, double y1,
        double x2, double y2,
        double width,
        string type = "StraightCenter",
        double x3 = 0, double y3 = 0,
        string? unit = null)
    {
        var doc = ActiveSwDoc
            ?? throw new InvalidOperationException("No active SolidWorks document.");
        var sm = doc.SketchManager
            ?? throw new InvalidOperationException("No SketchManager available.");
        if (sm.ActiveSketch is null)
        {
            throw new InvalidOperationException(
                "CreateSlot requires an active sketch. Call InsertSketchOnPlane first.");
        }

        int slotType = type?.Trim().ToLowerInvariant() switch
        {
            "straightcenter" or "straight" => 0,
            "straightthreepoint" or "straight3" => 1,
            "centerpointarc" or "arc" => 2,
            "threepointarc" or "arc3" => 3,
            _ => 0,
        };

        var mx1 = SwUnits.ToMeters(x1, unit, doc);
        var my1 = SwUnits.ToMeters(y1, unit, doc);
        var mx2 = SwUnits.ToMeters(x2, unit, doc);
        var my2 = SwUnits.ToMeters(y2, unit, doc);
        var mx3 = SwUnits.ToMeters(x3, unit, doc);
        var my3 = SwUnits.ToMeters(y3, unit, doc);
        var mw = SwUnits.ToMeters(width, unit, doc);

        sm.CreateSketchSlot(
            SlotCreationType: slotType,
            SlotLengthType: 0,
            Width: mw,
            X1: mx1, Y1: my1, Z1: 0,
            X2: mx2, Y2: my2, Z2: 0,
            X3: mx3, Y3: my3, Z3: 0,
            CenterArcDirection: 0,
            AddDimension: false);
    }

    [KernelFunction(nameof(CreateEllipse))]
    [Description("Create an ellipse in the active sketch from its centre, " +
        "a point on the major axis, and a point on the minor axis.")]
    public void CreateEllipse(
        double centerX, double centerY,
        double majorX, double majorY,
        double minorX, double minorY,
        string? unit = null)
    {
        var doc = ActiveSwDoc
            ?? throw new InvalidOperationException("No active SolidWorks document.");
        var sm = doc.SketchManager;
        if (sm?.ActiveSketch is null)
        {
            throw new InvalidOperationException("CreateEllipse requires an active sketch.");
        }
        var cx = SwUnits.ToMeters(centerX, unit, doc);
        var cy = SwUnits.ToMeters(centerY, unit, doc);
        var maX = SwUnits.ToMeters(majorX, unit, doc);
        var maY = SwUnits.ToMeters(majorY, unit, doc);
        var miX = SwUnits.ToMeters(minorX, unit, doc);
        var miY = SwUnits.ToMeters(minorY, unit, doc);

        sm.CreateEllipse(cx, cy, 0, maX, maY, 0, miX, miY, 0);
    }

    [KernelFunction(nameof(SketchTrim))]
    [Description("Trim a sketch entity. trimType: 'PowerTrim' (default), " +
        "'TrimClosest', 'TrimCorner', 'TrimEntireSegment'. x/y is the " +
        "pick point in document units.")]
    public void SketchTrim(double x, double y, string trimType = "PowerTrim", string? unit = null)
    {
        var doc = ActiveSwDoc
            ?? throw new InvalidOperationException("No active SolidWorks document.");
        var sm = doc.SketchManager;
        if (sm?.ActiveSketch is null)
        {
            throw new InvalidOperationException("SketchTrim requires an active sketch.");
        }
        int option = trimType?.Trim().ToLowerInvariant() switch
        {
            "powertrim" => 4,
            "trimclosest" or "closest" => 1,
            "trimcorner" or "corner" => 2,
            "trimentiresegment" or "segment" => 3,
            _ => 4,
        };
        var mx = SwUnits.ToMeters(x, unit, doc);
        var my = SwUnits.ToMeters(y, unit, doc);
        sm.SketchTrim(option, mx, my, 0);
    }

    [KernelFunction(nameof(ToggleConstructionGeometry))]
    [Description("Toggle the currently selected sketch entity between " +
        "regular and construction (reference) geometry.")]
    public void ToggleConstructionGeometry()
    {
        var doc = ActiveSwDoc
            ?? throw new InvalidOperationException("No active SolidWorks document.");
        var sm = doc.SketchManager
            ?? throw new InvalidOperationException("No SketchManager available.");
        sm.CreateConstructionGeometry();
    }

    [KernelFunction(nameof(LinearSketchPattern))]
    [Description("Step-and-repeat the currently selected sketch entities " +
        "into a linear pattern. spacingX/Y in document length unit, " +
        "angles in degrees.")]
    public void LinearSketchPattern(
        int countX, int countY,
        double spacingX, double spacingY,
        double angleXDegrees = 0,
        double angleYDegrees = 90,
        string? unit = null)
    {
        var doc = ActiveSwDoc
            ?? throw new InvalidOperationException("No active SolidWorks document.");
        var sm = doc.SketchManager;
        if (sm?.ActiveSketch is null)
        {
            throw new InvalidOperationException("LinearSketchPattern requires an active sketch.");
        }
        sm.CreateLinearSketchStepAndRepeat(
            NumX: Math.Max(1, countX),
            NumY: Math.Max(1, countY),
            SpacingX: SwUnits.ToMeters(spacingX, unit, doc),
            SpacingY: SwUnits.ToMeters(spacingY, unit, doc),
            AngleX: SwUnits.DegreesToRadians(angleXDegrees),
            AngleY: SwUnits.DegreesToRadians(angleYDegrees),
            DeleteInstances: string.Empty,
            XSpacingDim: false,
            YSpacingDim: false,
            AngleDim: false,
            CreateNumOfInstancesDimInXDir: false,
            CreateNumOfInstancesDimInYDir: false);
    }

    [KernelFunction(nameof(CircularSketchPattern))]
    [Description("Step-and-repeat the currently selected sketch entities " +
        "into a circular pattern.")]
    public void CircularSketchPattern(
        double arcRadius,
        int count,
        double totalAngleDegrees = 360,
        bool patternRotate = true,
        string? unit = null)
    {
        var doc = ActiveSwDoc
            ?? throw new InvalidOperationException("No active SolidWorks document.");
        var sm = doc.SketchManager;
        if (sm?.ActiveSketch is null)
        {
            throw new InvalidOperationException("CircularSketchPattern requires an active sketch.");
        }
        sm.CreateCircularSketchStepAndRepeat(
            ArcRadius: SwUnits.ToMeters(arcRadius, unit, doc),
            ArcAngle: SwUnits.DegreesToRadians(totalAngleDegrees),
            PatternNum: Math.Max(1, count),
            PatternSpacing: SwUnits.DegreesToRadians(totalAngleDegrees / Math.Max(1, count)),
            PatternRotate: patternRotate,
            DeleteInstances: string.Empty,
            RadiusDim: false,
            AngleDim: false,
            CreateNumOfInstancesDim: false);
    }
}

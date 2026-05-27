using Microsoft.SemanticKernel;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.ComponentModel;

namespace Copilot.Sw.Skills.Core;

/// <summary>
/// Feature-modifying operations: Fillet, Chamfer, MirrorFeature, Linear/
/// CircularPattern. The relevant edges / faces / features must already be
/// selected (use <c>SelectByName</c> or <c>SelectFaceAt</c>) before calling
/// these.
/// </summary>
public sealed class ModifyFeatureSkill : SldWorksSkillContext
{
    [KernelFunction(nameof(Fillet))]
    [Description("Add a constant-radius simple fillet to the currently " +
        "selected edges / faces. Radius is in the document's length unit.")]
    public string Fillet(double radius)
    {
        var (doc, fm) = RequireFeatureManager();
        var r = SwUnits.ToMeters(Math.Abs(radius), null, doc);
        var feat = fm.FeatureFillet3(
            /* Options */ (int)swFeatureFilletOptions_e.swFeatureFilletPropagate,
            /* R1 */ r, /* R2 */ 0, /* Rho */ 0,
            /* Ftyp */ (int)swFeatureFilletType_e.swFeatureFilletType_Simple,
            /* OverflowType */ (int)swFilletOverFlowType_e.swFilletOverFlowType_Default,
            /* ConicRhoType */ 0,
            /* Radii */ null, /* Dist2Arr */ null, /* RhoArr */ null,
            /* SetBackDistances */ null,
            /* PointRadiusArray */ null,
            /* PointDist2Array */ null,
            /* PointRhoArray */ null) as IFeature;
        return feat?.Name
            ?? throw new InvalidOperationException(
                "Fillet failed. Make sure one or more edges/faces are " +
                "selected (use SelectByName or SelectFaceAt first).");
    }

    [KernelFunction(nameof(Chamfer))]
    [Description("Add a distance-distance chamfer to the currently " +
        "selected edges. Distance is in the document's length unit.")]
    public string Chamfer(double distance)
    {
        var (doc, fm) = RequireFeatureManager();
        var d = SwUnits.ToMeters(Math.Abs(distance), null, doc);
        var feat = fm.InsertFeatureChamfer(
            /* Options */ (int)swFeatureChamferOption_e.swFeatureChamferKeepFeature,
            /* ChamferType */ (int)swChamferType_e.swChamferEqualDistance,
            /* Width */ d, /* Angle */ 0, /* OtherDist */ d,
            /* VertexChamDist1/2/3 */ 0, 0, 0) as IFeature;
        return feat?.Name
            ?? throw new InvalidOperationException(
                "Chamfer failed. Make sure one or more edges are selected.");
    }

    [KernelFunction(nameof(LinearPattern))]
    [Description("Create a linear pattern of the currently selected " +
        "feature(s) along edge/axis 'directionName'. Spacing is in the " +
        "document's length unit. Pre-select the seed feature(s) first.")]
    public string LinearPattern(
        string directionName,
        int instances,
        double spacing,
        bool flipDirection = false)
    {
        if (string.IsNullOrWhiteSpace(directionName))
        {
            throw new ArgumentException("directionName is required.", nameof(directionName));
        }
        if (instances < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(instances), "Instances must be >= 2.");
        }

        var (doc, fm) = RequireFeatureManager();
        var s = SwUnits.ToMeters(Math.Abs(spacing), null, doc);
        var feat = fm.FeatureLinearPattern3(
            instances, s, 1, 0,
            flipDirection, false,
            directionName, null,
            /* GeometryPattern */ false,
            /* VaryInstance */ false);
        return feat?.Name
            ?? throw new InvalidOperationException(
                $"LinearPattern failed. Verify direction '{directionName}' exists " +
                "and the seed feature(s) are selected.");
    }

    [KernelFunction(nameof(CircularPattern))]
    [Description("Create a circular pattern of the currently selected " +
        "feature(s) around the axis named 'axisName'. Spacing is the " +
        "total sweep angle in degrees when equalSpacing=true.")]
    public string CircularPattern(
        string axisName,
        int instances,
        double angleDegrees = 360.0,
        bool equalSpacing = true,
        bool flipDirection = false)
    {
        if (string.IsNullOrWhiteSpace(axisName))
        {
            throw new ArgumentException("axisName is required.", nameof(axisName));
        }
        if (instances < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(instances), "Instances must be >= 2.");
        }

        var (_, fm) = RequireFeatureManager();
        var rad = SwUnits.DegreesToRadians(angleDegrees);
        var feat = fm.FeatureCircularPattern4(
            instances, rad, flipDirection, axisName,
            /* GeometryPattern */ false,
            equalSpacing,
            /* VaryInstance */ false);
        return feat?.Name
            ?? throw new InvalidOperationException(
                $"CircularPattern failed. Verify axis '{axisName}' exists " +
                "and the seed feature(s) are selected.");
    }

    [KernelFunction(nameof(MirrorFeature))]
    [Description("Mirror the currently selected feature(s) about the " +
        "named plane (e.g. 'Front Plane' or 'Plane1'). Select the mirror " +
        "plane and the features to mirror, then call this.")]
    public string MirrorFeature()
    {
        var (_, fm) = RequireFeatureManager();
        var feat = fm.InsertMirrorFeature2(
            /* BMirrorBody */ false,
            /* BGeometryPattern */ false,
            /* BMerge */ true,
            /* BKnit */ false,
            /* ScopeOptions */ 0);
        return feat?.Name
            ?? throw new InvalidOperationException(
                "MirrorFeature failed. Make sure the mirror plane AND the " +
                "feature(s) to mirror are selected.");
    }

    private (IModelDoc2 doc, IFeatureManager fm) RequireFeatureManager()
    {
        var doc = ActiveSwDoc
            ?? throw new InvalidOperationException("No active SolidWorks document.");
        var fm = doc.FeatureManager
            ?? throw new InvalidOperationException("Active document has no FeatureManager.");
        return (doc, fm);
    }
}

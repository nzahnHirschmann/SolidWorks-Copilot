using Microsoft.SemanticKernel;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.ComponentModel;

namespace Copilot.Sw.Skills.Core;

/// <summary>
/// Higher-order solid features: Sweep, Loft, Shell, Draft, SimpleHole,
/// HoleWizard. Most of these require specific pre-selection — see each
/// method's description.
/// </summary>
public sealed class AdvancedFeatureSkill : SldWorksSkillContext
{
    [KernelFunction(nameof(Shell))]
    [Description("Shell the active part to the given wall thickness. " +
        "Faces selected before calling will be removed; otherwise the " +
        "whole body is shelled outward/inward.")]
    public string Shell(double thickness, bool outward = false, string? unit = null)
    {
        var doc = RequirePart();
        ExitAnyActiveSketch(doc);
        var t = SwUnits.ToMeters(thickness, unit, doc);
        doc.InsertFeatureShell(t, outward);
        return GetLastFeatureName(doc, "Shell");
    }

    [KernelFunction(nameof(Draft))]
    [Description("Apply a neutral-plane draft to selected faces. " +
        "Pre-select: the neutral plane (mark=1) and the faces to draft " +
        "(mark=2). Angle is in degrees.")]
    public string Draft(double angleDegrees, bool flipDirection = false)
    {
        var doc = RequirePart();
        ExitAnyActiveSketch(doc);
        var rad = SwUnits.DegreesToRadians(angleDegrees);
        var feat = doc.FeatureManager.InsertMultiFaceDraft(
            rad,
            flipDirection,
            EdgeDraft: false,
            PropType: 0,           // neutral plane
            IsStepDraft: false,
            IsBodyDraft: false) as IFeature
            ?? throw new InvalidOperationException(
                "Draft failed. Pre-select the neutral plane (mark=1) and " +
                "the faces to draft (mark=2).");
        return feat.Name;
    }

    [KernelFunction(nameof(SimpleHole))]
    [Description("Insert a simple hole through the active part. " +
        "Pre-select the planar face plus the sketch point that locates " +
        "the hole. Depth is ignored when throughAll is true.")]
    public string SimpleHole(
        double diameter,
        double depth = 0,
        bool throughAll = false,
        string? unit = null)
    {
        var doc = RequirePart();
        ExitAnyActiveSketch(doc);
        var d = SwUnits.ToMeters(diameter, unit, doc);
        var len = SwUnits.ToMeters(Math.Max(depth, 0.001), unit, doc);
        int endCond = throughAll
            ? (int)swEndConditions_e.swEndCondThroughAll
            : (int)swEndConditions_e.swEndCondBlind;

        var feat = doc.FeatureManager.SimpleHole2(
            d,
            false, false, true,
            endCond, 0,
            len, 0,
            false, false, false, false,
            0, 0,
            false, false, false, false,
            true, true,
            false, false, false) as IFeature
            ?? throw new InvalidOperationException(
                "SimpleHole failed. Make sure a planar face is selected.");
        return feat.Name;
    }

    [KernelFunction(nameof(Sweep))]
    [Description("Create a swept boss from the named profile sketch " +
        "swept along the named path sketch. Both sketches must already " +
        "exist in the part.")]
    public string Sweep(string profileSketch, string pathSketch, bool merge = true)
    {
        var doc = RequirePart();
        ExitAnyActiveSketch(doc);

        doc.ClearSelection2(true);
        if (!doc.Extension.SelectByID2(
                profileSketch, "SKETCH", 0, 0, 0, false,
                1, null, 0))
        {
            throw new InvalidOperationException(
                $"Could not select profile sketch '{profileSketch}'.");
        }
        if (!doc.Extension.SelectByID2(
                pathSketch, "SKETCH", 0, 0, 0, true,
                4, null, 0))
        {
            throw new InvalidOperationException(
                $"Could not select path sketch '{pathSketch}'.");
        }

        var feat = doc.FeatureManager.InsertProtrusionSwept3(
            Propagate: false,
            Alignment: false,
            TwistCtrlOption: 0,
            KeepTangency: false,
            BAdvancedSmoothing: false,
            StartMatchingType: 0,
            EndMatchingType: 0,
            IsThinBody: false,
            Thickness1: 0, Thickness2: 0,
            ThinType: 0,
            PathAlign: 0,
            Merge: merge,
            UseFeatScope: true,
            UseAutoSelect: true,
            TwistAngle: 0,
            BMergeSmoothFaces: true) as IFeature
            ?? throw new InvalidOperationException("Sweep failed.");
        return feat.Name;
    }

    [KernelFunction(nameof(SweepCut))]
    [Description("Create a swept cut from the named profile sketch " +
        "along the named path sketch.")]
    public string SweepCut(string profileSketch, string pathSketch)
    {
        var doc = RequirePart();
        ExitAnyActiveSketch(doc);
        doc.ClearSelection2(true);
        if (!doc.Extension.SelectByID2(profileSketch, "SKETCH", 0, 0, 0, false, 1, null, 0))
        {
            throw new InvalidOperationException($"Could not select profile sketch '{profileSketch}'.");
        }
        if (!doc.Extension.SelectByID2(pathSketch, "SKETCH", 0, 0, 0, true, 4, null, 0))
        {
            throw new InvalidOperationException($"Could not select path sketch '{pathSketch}'.");
        }

        var feat = doc.FeatureManager.InsertCutSwept3(
            false, false, 0, false, false, 0, 0,
            false, 0, 0, 0, 0,
            true, true, 0, true) as IFeature
            ?? throw new InvalidOperationException("SweepCut failed.");
        return feat.Name;
    }

    [KernelFunction(nameof(Loft))]
    [Description("Create a lofted boss from the listed profile sketches " +
        "(at least two). Profiles are connected in the order given.")]
    public string Loft(string[] profileSketches, bool merge = true, bool closed = false)
    {
        if (profileSketches is null || profileSketches.Length < 2)
        {
            throw new ArgumentException("Loft needs at least two profile sketches.", nameof(profileSketches));
        }
        var doc = RequirePart();
        ExitAnyActiveSketch(doc);
        doc.ClearSelection2(true);
        for (int i = 0; i < profileSketches.Length; i++)
        {
            if (!doc.Extension.SelectByID2(
                    profileSketches[i], "SKETCH", 0, 0, 0,
                    Append: i > 0,
                    Mark: 1,
                    Callout: null,
                    SelectOption: 0))
            {
                throw new InvalidOperationException(
                    $"Could not select profile sketch '{profileSketches[i]}'.");
            }
        }

        var feat = doc.FeatureManager.InsertProtrusionBlend2(
            Closed: closed,
            KeepTangency: false,
            ForceNonRational: false,
            TessToleranceFactor: 1.0,
            StartMatchingType: 0,
            EndMatchingType: 0,
            StartTangentLength: 1.0,
            EndTangentLength: 1.0,
            StartTangentDir: false,
            EndTangentDir: false,
            IsThinBody: false,
            Thickness1: 0, Thickness2: 0,
            ThinType: 0,
            Merge: merge,
            UseFeatScope: true,
            UseAutoSelect: true,
            GuideCurveInfluence: 0) as IFeature
            ?? throw new InvalidOperationException("Loft failed.");
        return feat.Name;
    }

    [KernelFunction(nameof(LoftCut))]
    [Description("Create a lofted cut from the listed profile sketches.")]
    public string LoftCut(string[] profileSketches, bool closed = false)
    {
        if (profileSketches is null || profileSketches.Length < 2)
        {
            throw new ArgumentException("LoftCut needs at least two profile sketches.", nameof(profileSketches));
        }
        var doc = RequirePart();
        ExitAnyActiveSketch(doc);
        doc.ClearSelection2(true);
        for (int i = 0; i < profileSketches.Length; i++)
        {
            doc.Extension.SelectByID2(profileSketches[i], "SKETCH", 0, 0, 0, i > 0, 1, null, 0);
        }

        var feat = doc.FeatureManager.InsertCutBlend(
            closed, false, false, 1.0,
            0, 0, false, 0, 0, 0, true, true) as IFeature
            ?? throw new InvalidOperationException("LoftCut failed.");
        return feat.Name;
    }

    private IModelDoc2 RequirePart()
    {
        var doc = ActiveSwDoc
            ?? throw new InvalidOperationException("No active SolidWorks document.");
        if ((swDocumentTypes_e)doc.GetType() != swDocumentTypes_e.swDocPART)
        {
            throw new InvalidOperationException("This feature requires an active part document.");
        }
        return doc;
    }

    private static void ExitAnyActiveSketch(IModelDoc2 doc)
    {
        if (doc.SketchManager?.ActiveSketch is not null)
        {
            doc.SketchManager.InsertSketch(true);
        }
    }

    private static string GetLastFeatureName(IModelDoc2 doc, string fallback)
    {
        if (doc.SelectionManager is ISelectionMgr sm
            && sm.GetSelectedObject6(1, -1) is IFeature f)
        {
            return f.Name;
        }
        return fallback;
    }
}

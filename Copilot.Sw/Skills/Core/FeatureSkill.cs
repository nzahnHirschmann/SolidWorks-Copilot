using Microsoft.SemanticKernel;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.ComponentModel;

namespace Copilot.Sw.Skills.Core;

/// <summary>
/// Boss / cut features driven by the most-recent sketch on the part.
/// Each function exits any open sketch first (the SolidWorks feature
/// managers expect a committed sketch as their selection).
/// </summary>
public sealed class FeatureSkill : SldWorksSkillContext
{
    private const int EndCondBlind = (int)swEndConditions_e.swEndCondBlind;
    private const int EndCondThrough = (int)swEndConditions_e.swEndCondThroughAll;

    [KernelFunction(nameof(Extrude))]
    [Description("Extrude the most-recently-created sketch into a solid " +
        "boss by the given depth (in the document's length unit). " +
        "Pass reverse=true to extrude in the opposite direction.")]
    public string Extrude(double depth, bool reverse = false, bool merge = true)
    {
        var (doc, fm) = RequirePartFeatureManager();
        var d = SwUnits.ToMeters(Math.Abs(depth), null, doc);
        ExitAnyActiveSketch(doc);

        var feat = fm.FeatureExtrusion3(
            /* Sd */ true, /* Flip */ reverse, /* Dir */ true,
            /* T1 */ EndCondBlind, /* T2 */ EndCondBlind,
            /* D1 */ d, /* D2 */ 0,
            /* Dchk1/2 */ false, false,
            /* Ddir1/2 */ false, false,
            /* Dang1/2 */ 0, 0,
            /* OffsetReverse1/2 */ false, false,
            /* TranslateSurface1/2 */ false, false,
            /* Merge */ merge,
            /* UseFeatScope */ true,
            /* UseAutoSelect */ true,
            /* T0 */ (int)swStartConditions_e.swStartSketchPlane,
            /* StartOffset */ 0,
            /* FlipStartOffset */ false);
        return RequireFeatureName(feat, "Extrude");
    }

    [KernelFunction(nameof(ExtrudeCut))]
    [Description("Extrude-cut the most-recently-created sketch by the " +
        "given depth. Pass throughAll=true to ignore the depth and cut " +
        "through the whole body.")]
    public string ExtrudeCut(double depth, bool throughAll = false, bool reverse = false)
    {
        var (doc, fm) = RequirePartFeatureManager();
        var d = SwUnits.ToMeters(Math.Abs(depth), null, doc);
        ExitAnyActiveSketch(doc);

        var feat = fm.FeatureCut4(
            /* Sd */ true, /* Flip */ reverse, /* Dir */ true,
            /* T1 */ throughAll ? EndCondThrough : EndCondBlind,
            /* T2 */ EndCondBlind,
            /* D1 */ d, /* D2 */ 0,
            /* Dchk1/2 */ false, false,
            /* Ddir1/2 */ false, false,
            /* Dang1/2 */ 0, 0,
            /* OffsetReverse1/2 */ false, false,
            /* TranslateSurface1/2 */ false, false,
            /* NormalCut */ true,
            /* UseFeatScope */ true,
            /* UseAutoSelect */ true,
            /* AssemblyFeatureScope */ false,
            /* AutoSelectComponents */ false,
            /* PropagateFeatureToParts */ false,
            /* T0 */ (int)swStartConditions_e.swStartSketchPlane,
            /* StartOffset */ 0,
            /* FlipStartOffset */ false,
            /* OptimizeGeometry */ true);
        return RequireFeatureName(feat, "ExtrudeCut");
    }

    [KernelFunction(nameof(Revolve))]
    [Description("Revolve the most-recently-created sketch around its " +
        "first construction-line axis by the given angle (degrees). " +
        "Pass cut=true to revolve-cut instead of creating a boss.")]
    public string Revolve(double angleDegrees = 360.0, bool cut = false, bool reverse = false)
    {
        var (_, fm) = RequirePartFeatureManager();
        ExitAnyActiveSketch(ActiveSwDoc!);

        var angle = SwUnits.DegreesToRadians(Math.Abs(angleDegrees));
        var feat = fm.FeatureRevolve2(
            /* SingleDir */ true,
            /* IsSolid */ true,
            /* IsThin */ false,
            /* IsCut */ cut,
            /* ReverseDir */ reverse,
            /* BothDirectionUpToSameEntity */ false,
            /* Dir1Type */ (int)swEndConditions_e.swEndCondBlind,
            /* Dir2Type */ (int)swEndConditions_e.swEndCondBlind,
            /* Dir1Angle */ angle, /* Dir2Angle */ 0,
            /* OffsetReverse1/2 */ false, false,
            /* OffsetDistance1/2 */ 0, 0,
            /* ThinType */ 0,
            /* ThinThickness1/2 */ 0, 0,
            /* Merge */ true,
            /* UseFeatScope */ true,
            /* UseAutoSelect */ true);
        return RequireFeatureName(feat, cut ? "RevolveCut" : "Revolve");
    }

    private (IModelDoc2 doc, IFeatureManager fm) RequirePartFeatureManager()
    {
        var doc = ActiveSwDoc
            ?? throw new InvalidOperationException("No active SolidWorks document.");
        if ((swDocumentTypes_e)doc.GetType() != swDocumentTypes_e.swDocPART)
        {
            throw new InvalidOperationException(
                "Feature skills require an active part document.");
        }
        var fm = doc.FeatureManager
            ?? throw new InvalidOperationException("Active document has no FeatureManager.");
        return (doc, fm);
    }

    private static void ExitAnyActiveSketch(IModelDoc2 doc)
    {
        if (doc.SketchManager?.ActiveSketch is not null)
        {
            doc.SketchManager.InsertSketch(true);
        }
    }

    private static string RequireFeatureName(IFeature? feat, string featureKind)
    {
        if (feat is null)
        {
            throw new InvalidOperationException(
                $"{featureKind} failed. Make sure the most recent sketch is " +
                "valid (closed contour for solids, profile + axis for revolves).");
        }
        return feat.Name;
    }
}

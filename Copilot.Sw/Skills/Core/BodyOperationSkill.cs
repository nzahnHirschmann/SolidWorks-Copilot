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

    [KernelFunction(nameof(KnitSurfaces))]
    [Description("Knit (sew) the currently selected surfaces into a single " +
        "surface body. Pre-select two or more surfaces sharing edges. " +
        "If 'knitToBody' is true and the knit is closed, the result is " +
        "promoted to a solid body. 'gapToleranceMm' sets the allowable gap.")]
    public string KnitSurfaces(
        bool mergeEntities = true,
        bool knitToBody = false,
        double gapToleranceMm = 0.01,
        bool tryToForm = true)
    {
        var doc = ActiveSwDoc
            ?? throw new InvalidOperationException("No active SolidWorks document.");
        var tol = SwUnits.ToMeters(gapToleranceMm, "mm", doc);
        var feat = doc.FeatureManager.InsertSewRefSurface(
            tryToForm, mergeEntities, knitToBody, tol, 0) as IFeature
            ?? throw new InvalidOperationException(
                "KnitSurfaces failed. Pre-select two or more surfaces sharing edges.");
        return feat.Name;
    }

    [KernelFunction(nameof(SplitBody))]
    [Description("Split the currently selected solid body using the also-" +
        "selected trimming entities (planes / surfaces / sketches). Returns " +
        "the new Split feature name. Pre-selection order: the solid body to " +
        "split, then every trimming entity. Set 'consumeOriginal' to remove " +
        "the source body after splitting (it is kept by default so the " +
        "resulting bodies can be individually deleted later).")]
    public string SplitBody(bool consumeOriginal = false)
    {
        var doc = ActiveSwDoc
            ?? throw new InvalidOperationException("No active SolidWorks document.");
        if (doc is not IPartDoc)
        {
            throw new InvalidOperationException("SplitBody requires an active part document.");
        }
        if (doc.SelectionManager is not ISelectionMgr sm
            || sm.GetSelectedObjectCount2(-1) < 2)
        {
            throw new InvalidOperationException(
                "Pre-select the solid body to split followed by at least one trimming entity.");
        }

        var fm = doc.FeatureManager;
        // PreSplitBody2 returns the array of resulting bodies; mark them all
        // for retention by passing the entire array back to PostSplitBody2.
        var resultingBodies = fm.PreSplitBody2();
        if (resultingBodies is null)
        {
            throw new InvalidOperationException(
                "PreSplitBody2 returned no bodies. Verify the body and trimming " +
                "entities are selected and that the trim would actually divide the body.");
        }

        // bodiesToMark = same array (keep them all);
        // origins / savePaths / overrideTemplateName = null (no file-per-body save).
        var postResult = fm.PostSplitBody2(resultingBodies, ConsumeCut: consumeOriginal,
            Origins: null, SavePaths: null, OverrideTemplateName: string.Empty);
        if (postResult is null)
        {
            throw new InvalidOperationException("PostSplitBody2 failed.");
        }

        // The new feature is the last item in the FeatureManager tree.
        var last = LastFeatureName(doc) ?? "Split";
        return last;
    }

    [KernelFunction(nameof(TrimSurface))]
    [Description("Trim the currently selected surfaces against each other " +
        "(mutual trim) or against a selected trim tool (standard trim). " +
        "Pre-selection: the surface(s) to trim, then the trim tool, then " +
        "the face/piece to keep or remove. Set 'mutualTrim' true for two " +
        "surfaces that trim each other; otherwise standard trim is used. " +
        "'removePicked' true removes the highlighted pieces, false keeps them. " +
        "'sewSurface' knits the result automatically.")]
    public string TrimSurface(
        bool mutualTrim = false,
        bool removePicked = true,
        bool sewSurface = false)
    {
        var doc = ActiveSwDoc
            ?? throw new InvalidOperationException("No active SolidWorks document.");
        if (doc.SelectionManager is not ISelectionMgr sm
            || sm.GetSelectedObjectCount2(-1) < 2)
        {
            throw new InvalidOperationException(
                "Pre-select the surface(s) to trim plus the trim tool / pieces.");
        }

        var fm = doc.FeatureManager;
        if (!fm.PreTrimSurface(mutualTrim,
                BSplitSystemIn: false,
                BSplitLinearIn: false,
                BRemovePickedIn: removePicked))
        {
            throw new InvalidOperationException(
                "PreTrimSurface failed. Check selection: needs at least one surface plus a trim tool.");
        }
        var feat = fm.PostTrimSurface(sewSurface) as IFeature
            ?? throw new InvalidOperationException("PostTrimSurface failed.");
        return feat.Name;
    }

    private static string? LastFeatureName(IModelDoc2 doc)
    {
        string? name = null;
        var feat = doc.FirstFeature() as IFeature;
        int safety = 0;
        while (feat is not null && safety++ < 5000)
        {
            name = feat.Name;
            feat = feat.GetNextFeature() as IFeature;
        }
        return name;
    }
}

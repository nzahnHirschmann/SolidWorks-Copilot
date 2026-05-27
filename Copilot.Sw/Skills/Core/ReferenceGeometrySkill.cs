using Microsoft.SemanticKernel;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.ComponentModel;

namespace Copilot.Sw.Skills.Core;

/// <summary>
/// Reference geometry: planes, axes. These are the anchors most non-trivial
/// modelling plans need before they can sketch on something other than
/// the three default planes.
/// </summary>
public sealed class ReferenceGeometrySkill : SldWorksSkillContext
{
    [KernelFunction(nameof(CreateOffsetPlane))]
    [Description("Create a reference plane offset from an existing plane " +
        "or planar face by 'distance' (in the document's length unit). " +
        "Pre-select the source plane via SelectByName(planeName, 'PLANE') " +
        "OR pass its name in 'sourcePlane' and this will select it.")]
    public string CreateOffsetPlane(string sourcePlane, double distance, bool flip = false)
    {
        if (string.IsNullOrWhiteSpace(sourcePlane))
        {
            throw new ArgumentException("sourcePlane is required.", nameof(sourcePlane));
        }
        var doc = RequireActiveDoc();
        var d = SwUnits.ToMeters(Math.Abs(distance), null, doc);

        doc.ClearSelection2(true);
        var ok = doc.Extension.SelectByID2(
            sourcePlane, "PLANE", 0, 0, 0, false, 0, null,
            (int)swSelectOption_e.swSelectOptionDefault);
        if (!ok)
        {
            // Could be a planar face named under FACE — let the model retry.
            throw new InvalidOperationException(
                $"Plane '{sourcePlane}' not found.");
        }

        var fm = doc.FeatureManager;
        var feat = fm.InsertRefPlane(
            /* FirstConstraint */ (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_Distance
                                | (flip ? (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_OptionFlip : 0),
            /* FirstConstraintAngleOrDistance */ d,
            /* SecondConstraint */ 0,
            /* SecondConstraintAngleOrDistance */ 0,
            /* ThirdConstraint */ 0,
            /* ThirdConstraintAngleOrDistance */ 0) as IFeature;
        return feat?.Name
            ?? throw new InvalidOperationException(
                "InsertRefPlane failed. The selected entity may not be a " +
                "valid plane reference.");
    }

    private IModelDoc2 RequireActiveDoc()
    {
        return ActiveSwDoc
            ?? throw new InvalidOperationException("No active SolidWorks document.");
    }

    [KernelFunction(nameof(CreateAxis))]
    [Description("Create a reference axis. Pre-select the entities that " +
        "define the axis (one edge, two planes, two points, or a " +
        "cylindrical/conical face). Returns the new axis's name.")]
    public string CreateAxis(bool autoSize = true)
    {
        var doc = RequireActiveDoc();
        doc.InsertAxis2(autoSize);
        if (doc.SelectionManager is ISelectionMgr sm
            && sm.GetSelectedObject6(1, -1) is IFeature feat)
        {
            return feat.Name;
        }
        return "Axis";
    }

    [KernelFunction(nameof(CreateReferencePoint))]
    [Description("Create a reference point. Pre-select the geometry it " +
        "should be derived from (vertex, edge midpoint, face centre, etc.).")]
    public string CreateReferencePoint()
    {
        var doc = RequireActiveDoc();
        var feat = doc.FeatureManager.InsertReferencePoint(
            (int)swRefPointType_e.swRefPointFaceCenter,
            0,
            0,
            1) as IFeature;
        return feat?.Name
            ?? throw new InvalidOperationException(
                "CreateReferencePoint failed. Pre-select a valid entity.");
    }

    [KernelFunction(nameof(CreateCoordinateSystem))]
    [Description("Create a coordinate system feature at the currently " +
        "selected origin (and optionally axis-defining entities).")]
    public string CreateCoordinateSystem(bool flipX = false, bool flipY = false, bool flipZ = false)
    {
        var doc = RequireActiveDoc();
        if (!doc.InsertCoordinateSystem(flipX, flipY, flipZ))
        {
            throw new InvalidOperationException(
                "InsertCoordinateSystem failed. Pre-select an origin point " +
                "(and optionally axis-defining entities).");
        }
        if (doc.SelectionManager is ISelectionMgr sm
            && sm.GetSelectedObject6(1, -1) is IFeature f)
        {
            return f.Name;
        }
        return "Coordinate System";
    }
}

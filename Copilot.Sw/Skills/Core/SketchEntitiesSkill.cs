using Microsoft.SemanticKernel;
using SolidWorks.Interop.sldworks;
using System;
using System.ComponentModel;

namespace Copilot.Sw.Skills.Core;

/// <summary>
/// 2D sketch primitives. Every coordinate argument is interpreted in the
/// active document's length unit (mm, in, …) — call
/// <c>GetDocumentUnits</c> first if you're unsure. The active sketch must
/// already exist; use <c>InsertSketchOnPlane</c> to start one.
/// </summary>
public sealed class SketchEntitiesSkill : SldWorksSkillContext
{
    [KernelFunction(nameof(CreateLine))]
    [Description("Draw a line segment in the active sketch from (x1,y1) to " +
        "(x2,y2). The Z coordinate is ignored for 2D sketches.")]
    public void CreateLine(double x1, double y1, double x2, double y2)
    {
        var sm = RequireSketchManager();
        var (doc, _) = SketchDoc();
        sm.CreateLine(
            SwUnits.ToMeters(x1, null, doc), SwUnits.ToMeters(y1, null, doc), 0,
            SwUnits.ToMeters(x2, null, doc), SwUnits.ToMeters(y2, null, doc), 0);
    }

    [KernelFunction(nameof(CreateRectangle))]
    [Description("Draw a corner-defined rectangle in the active sketch " +
        "from (x1,y1) to (x2,y2).")]
    public void CreateRectangle(double x1, double y1, double x2, double y2)
    {
        var sm = RequireSketchManager();
        var (doc, _) = SketchDoc();
        sm.CreateCornerRectangle(
            SwUnits.ToMeters(x1, null, doc), SwUnits.ToMeters(y1, null, doc), 0,
            SwUnits.ToMeters(x2, null, doc), SwUnits.ToMeters(y2, null, doc), 0);
    }

    [KernelFunction(nameof(CreateCenterRectangle))]
    [Description("Draw a center-defined rectangle in the active sketch " +
        "with center (cx,cy) and one corner at (x,y).")]
    public void CreateCenterRectangle(double cx, double cy, double x, double y)
    {
        var sm = RequireSketchManager();
        var (doc, _) = SketchDoc();
        sm.CreateCenterRectangle(
            SwUnits.ToMeters(cx, null, doc), SwUnits.ToMeters(cy, null, doc), 0,
            SwUnits.ToMeters(x, null, doc), SwUnits.ToMeters(y, null, doc), 0);
    }

    [KernelFunction(nameof(CreatePolygon))]
    [Description("Draw a regular polygon centered at (cx,cy) with the " +
        "given number of sides. The radius is measured to a vertex " +
        "(inscribed=false) or to a side mid-point (inscribed=true).")]
    public void CreatePolygon(
        double cx,
        double cy,
        double radius,
        int sides,
        bool inscribed = false)
    {
        if (sides < 3)
        {
            throw new ArgumentOutOfRangeException(nameof(sides), "Sides must be >= 3.");
        }
        var sm = RequireSketchManager();
        var (doc, _) = SketchDoc();
        var mc = SwUnits.ToMeters(cx, null, doc);
        var mcy = SwUnits.ToMeters(cy, null, doc);
        var mr = SwUnits.ToMeters(radius, null, doc);
        sm.CreatePolygon(mc, mcy, 0, mc + mr, mcy, 0, sides, inscribed);
    }

    [KernelFunction(nameof(CreateCircle))]
    [Description("Draw a circle in the active sketch with center (cx,cy) " +
        "and the given radius (in the document's length unit).")]
    public void CreateCircle(double cx, double cy, double radius)
    {
        var sm = RequireSketchManager();
        var (doc, _) = SketchDoc();
        sm.CreateCircleByRadius(
            SwUnits.ToMeters(cx, null, doc),
            SwUnits.ToMeters(cy, null, doc),
            0,
            SwUnits.ToMeters(radius, null, doc));
    }

    [KernelFunction(nameof(CreateArc))]
    [Description("Draw a 3-point arc in the active sketch with center " +
        "(cx,cy), starting at (x1,y1) and ending at (x2,y2). " +
        "Direction +1 = counter-clockwise, -1 = clockwise.")]
    public void CreateArc(
        double cx, double cy,
        double x1, double y1,
        double x2, double y2,
        short direction = +1)
    {
        var sm = RequireSketchManager();
        var (doc, _) = SketchDoc();
        sm.CreateArc(
            SwUnits.ToMeters(cx, null, doc), SwUnits.ToMeters(cy, null, doc), 0,
            SwUnits.ToMeters(x1, null, doc), SwUnits.ToMeters(y1, null, doc), 0,
            SwUnits.ToMeters(x2, null, doc), SwUnits.ToMeters(y2, null, doc), 0,
            direction);
    }

    [KernelFunction(nameof(CreatePoint))]
    [Description("Place a sketch point at (x,y) in the active sketch.")]
    public void CreatePoint(double x, double y)
    {
        var sm = RequireSketchManager();
        var (doc, _) = SketchDoc();
        sm.CreatePoint(
            SwUnits.ToMeters(x, null, doc),
            SwUnits.ToMeters(y, null, doc),
            0);
    }

    [KernelFunction(nameof(SketchOffset))]
    [Description("Offset the currently selected sketch entities by the " +
        "given signed distance. Pass chain=true to offset a connected " +
        "chain instead of single entities.")]
    public void SketchOffset(double distance, bool chain = true, bool construction = false)
    {
        var sm = RequireSketchManager();
        var (doc, _) = SketchDoc();
        var d = SwUnits.ToMeters(Math.Abs(distance), null, doc);
        sm.SketchOffset(d, distance < 0, chain, false, construction, false);
    }

    private (IModelDoc2 doc, ISketchManager sm) SketchDoc()
    {
        var doc = ActiveSwDoc
            ?? throw new InvalidOperationException("No active SolidWorks document.");
        var sm = doc.SketchManager
            ?? throw new InvalidOperationException("Active document has no SketchManager.");
        return (doc, sm);
    }

    private ISketchManager RequireSketchManager()
    {
        var (_, sm) = SketchDoc();
        if (sm.ActiveSketch is null)
        {
            throw new InvalidOperationException(
                "No active sketch. Call InsertSketchOnPlane first.");
        }
        return sm;
    }
}

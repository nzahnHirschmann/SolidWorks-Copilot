using Microsoft.SemanticKernel;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text.Json;

namespace Copilot.Sw.Skills.Core;

/// <summary>
/// Read-only introspection: lets the model *see* the model. Feature tree,
/// selection details, custom properties, measurements, bounding box, and
/// screenshots. Without these the LLM can only blindly emit modelling
/// calls; with them it can self-correct and reason about an existing part.
/// </summary>
public sealed class IntrospectionSkill : SldWorksSkillContext
{
    // -------- Feature tree --------

    [KernelFunction(nameof(GetFeatureTree))]
    [Description("Return a JSON tree of every feature in the active part " +
        "or assembly: name, type, suppression state, and child sub-features. " +
        "Use 'maxDepth' to limit recursion (default 4). This is the model's " +
        "main read-side window into an existing document.")]
    public string GetFeatureTree(int maxDepth = 4)
    {
        var doc = ActiveSwDoc
            ?? throw new InvalidOperationException("No active SolidWorks document.");

        var roots = new List<object>();
        for (var f = doc.FirstFeature() as IFeature; f is not null; f = f.GetNextFeature() as IFeature)
        {
            roots.Add(FeatureNode(f, 0, maxDepth));
        }

        return JsonSerializer.Serialize(new
        {
            doc = doc.GetTitle(),
            featureCount = roots.Count,
            features = roots,
        });
    }

    private static object FeatureNode(IFeature feat, int depth, int maxDepth)
    {
        var children = new List<object>();
        if (depth < maxDepth)
        {
            for (var sub = feat.GetFirstSubFeature() as IFeature;
                 sub is not null;
                 sub = sub.GetNextSubFeature() as IFeature)
            {
                children.Add(FeatureNode(sub, depth + 1, maxDepth));
            }
        }
        return new
        {
            name = feat.Name,
            type = feat.GetTypeName2(),
            suppressed = feat.IsSuppressed(),
            children,
        };
    }

    // -------- Selection details --------

    [KernelFunction(nameof(GetSelectedEntity))]
    [Description("Return JSON metadata for the first currently-selected " +
        "entity: SolidWorks selection type, name, and (where applicable) " +
        "feature type. Returns null when nothing is selected.")]
    public string GetSelectedEntity()
    {
        var doc = ActiveSwDoc
            ?? throw new InvalidOperationException("No active SolidWorks document.");
        if (doc.SelectionManager is not ISelectionMgr sm
            || sm.GetSelectedObjectCount2(-1) < 1)
        {
            return "null";
        }

        var raw = sm.GetSelectedObject6(1, -1);
        var typeId = sm.GetSelectedObjectType3(1, -1);
        string typeName = ((swSelectType_e)typeId).ToString();
        string? name = null;
        string? featureType = null;

        switch (raw)
        {
            case IFeature feat:
                name = feat.Name;
                featureType = feat.GetTypeName2();
                break;
            case IComponent2 comp:
                name = comp.Name2;
                break;
            case ISketch sketch when (sketch as IFeature) is IFeature sf:
                name = sf.Name;
                break;
        }

        return JsonSerializer.Serialize(new
        {
            type = typeName,
            typeId,
            name,
            featureType,
        });
    }

    // -------- Custom properties --------

    [KernelFunction(nameof(GetCustomProperties))]
    [Description("Return JSON dictionary of custom properties on the active " +
        "document. Pass 'configurationName' to read configuration-specific " +
        "properties; omit (or empty) for file-level (custom tab) properties.")]
    public string GetCustomProperties(string configurationName = "")
    {
        var doc = ActiveSwDoc
            ?? throw new InvalidOperationException("No active SolidWorks document.");
        var mgr = doc.Extension.CustomPropertyManager[configurationName ?? string.Empty];

        var names = mgr.GetNames() as string[] ?? Array.Empty<string>();
        var items = new List<object>();
        foreach (var n in names)
        {
            mgr.Get5(n, false, out string val, out string resolved, out _);
            items.Add(new
            {
                name = n,
                value = val,
                resolved,
                type = ((swCustomInfoType_e)mgr.GetType2(n)).ToString(),
            });
        }

        return JsonSerializer.Serialize(new
        {
            configuration = string.IsNullOrEmpty(configurationName) ? "<file>" : configurationName,
            count = items.Count,
            properties = items,
        });
    }

    [KernelFunction(nameof(SetCustomProperty))]
    [Description("Add or update a custom property on the active document. " +
        "'type' is one of TEXT, DATE, NUMBER, YESNO (default TEXT). Pass " +
        "'configurationName' to set configuration-specific values.")]
    public string SetCustomProperty(string name, string value, string type = "TEXT", string configurationName = "")
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Property name is required.", nameof(name));
        }
        var doc = ActiveSwDoc
            ?? throw new InvalidOperationException("No active SolidWorks document.");
        var mgr = doc.Extension.CustomPropertyManager[configurationName ?? string.Empty];

        var typeEnum = type?.Trim().ToUpperInvariant() switch
        {
            "DATE" => swCustomInfoType_e.swCustomInfoDate,
            "NUMBER" => swCustomInfoType_e.swCustomInfoNumber,
            "YESNO" => swCustomInfoType_e.swCustomInfoYesOrNo,
            _ => swCustomInfoType_e.swCustomInfoText,
        };

        var result = mgr.Add3(name, (int)typeEnum, value ?? string.Empty,
            (int)swCustomPropertyAddOption_e.swCustomPropertyDeleteAndAdd);
        return $"Set '{name}' = '{value}' ({typeEnum}, result={result}).";
    }

    // -------- Measurement --------

    [KernelFunction(nameof(MeasureSelection))]
    [Description("Run the SolidWorks Measure tool against the current " +
        "selection (1-2 entities: edges, faces, vertices, planes). Returns " +
        "JSON with length, distance, angle, area, perimeter, radius, " +
        "diameter, deltaX/Y/Z — populated fields depend on the selection.")]
    public string MeasureSelection()
    {
        var doc = ActiveSwDoc
            ?? throw new InvalidOperationException("No active SolidWorks document.");
        var m = doc.Extension.CreateMeasure();
        if (m is null)
        {
            throw new InvalidOperationException("CreateMeasure() returned null.");
        }
        if (!m.Calculate(null))
        {
            throw new InvalidOperationException(
                "Measure failed. Select 1 or 2 edges/faces/vertices first.");
        }
        return JsonSerializer.Serialize(new
        {
            length = m.Length,
            distance = m.Distance,
            normalDistance = m.NormalDistance,
            centerDistance = m.CenterDistance,
            angleRadians = m.Angle,
            angleDegrees = m.Angle * (180.0 / Math.PI),
            area = m.Area,
            perimeter = m.Perimeter,
            radius = m.Radius,
            diameter = m.Diameter,
            arcLength = m.ArcLength,
            chordLength = m.ChordLength,
            deltaX = m.DeltaX,
            deltaY = m.DeltaY,
            deltaZ = m.DeltaZ,
            isParallel = m.IsParallel,
            isPerpendicular = m.IsPerpendicular,
            isIntersect = m.IsIntersect,
        });
    }

    // -------- Bounding box --------

    [KernelFunction(nameof(GetBoundingBox))]
    [Description("Return the axis-aligned bounding box of the active part " +
        "in millimetres: { xMinMm, yMinMm, zMinMm, xMaxMm, yMaxMm, zMaxMm, " +
        "widthMm, depthMm, heightMm }. Part documents only.")]
    public string GetBoundingBox()
    {
        var doc = ActiveSwDoc
            ?? throw new InvalidOperationException("No active SolidWorks document.");
        if (doc is not IPartDoc part)
        {
            throw new InvalidOperationException(
                "GetBoundingBox requires an active part document.");
        }
        var box = part.GetPartBox(false) as double[]
            ?? throw new InvalidOperationException("GetPartBox returned no data.");
        if (box.Length < 6)
        {
            throw new InvalidOperationException("GetPartBox returned malformed data.");
        }
        return JsonSerializer.Serialize(new
        {
            xMinMm = Math.Round(box[0] * 1000, 3),
            yMinMm = Math.Round(box[1] * 1000, 3),
            zMinMm = Math.Round(box[2] * 1000, 3),
            xMaxMm = Math.Round(box[3] * 1000, 3),
            yMaxMm = Math.Round(box[4] * 1000, 3),
            zMaxMm = Math.Round(box[5] * 1000, 3),
            widthMm = Math.Round((box[3] - box[0]) * 1000, 3),
            depthMm = Math.Round((box[4] - box[1]) * 1000, 3),
            heightMm = Math.Round((box[5] - box[2]) * 1000, 3),
        });
    }

    // -------- Screenshot --------

    [KernelFunction(nameof(Screenshot))]
    [Description("Save a bitmap of the active document's graphics window to " +
        "the given path (PNG/BMP/JPG by extension). Returns the path. " +
        "If 'path' is empty, writes to %TEMP%\\sw-copilot-<title>.png. " +
        "Use this to let a vision-capable model literally see the part.")]
    public string Screenshot(string path = "", int width = 1024, int height = 768)
    {
        var doc = ActiveSwDoc
            ?? throw new InvalidOperationException("No active SolidWorks document.");
        if (width < 16 || height < 16)
        {
            throw new ArgumentException("width/height must be >= 16.");
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            var safeTitle = string.Join("_", (doc.GetTitle() ?? "doc").Split(Path.GetInvalidFileNameChars()));
            path = Path.Combine(Path.GetTempPath(), $"sw-copilot-{safeTitle}.png");
        }

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        if (!doc.SaveBMP(path, width, height))
        {
            throw new InvalidOperationException(
                $"SaveBMP failed for '{path}'. Verify the path is writable.");
        }
        return path;
    }

    // -------- Sketch entities --------

    [KernelFunction(nameof(GetSketchEntities))]
    [Description("Return JSON describing every segment and point of a " +
        "sketch: type (LINE/ARC/ELLIPSE/SPLINE/POINT), name, construction " +
        "flag, length (mm) and endpoints. Pass 'sketchName' to inspect a " +
        "named sketch; omit to use the currently-edited sketch.")]
    public string GetSketchEntities(string sketchName = "")
    {
        var doc = ActiveSwDoc
            ?? throw new InvalidOperationException("No active SolidWorks document.");

        ISketch? sketch;
        if (string.IsNullOrWhiteSpace(sketchName))
        {
            sketch = doc.SketchManager?.ActiveSketch as ISketch
                ?? throw new InvalidOperationException(
                    "No active sketch. Pass 'sketchName' or edit a sketch first.");
        }
        else
        {
            if (!doc.Extension.SelectByID2(sketchName, "SKETCH", 0, 0, 0, false, 0, null, 0))
            {
                throw new InvalidOperationException(
                    $"Sketch '{sketchName}' not found.");
            }
            var sm = doc.SelectionManager as ISelectionMgr
                ?? throw new InvalidOperationException("SelectionManager unavailable.");
            var feat = sm.GetSelectedObject6(1, -1) as IFeature
                ?? throw new InvalidOperationException(
                    $"'{sketchName}' is not a sketch feature.");
            sketch = feat.GetSpecificFeature2() as ISketch
                ?? throw new InvalidOperationException(
                    $"'{sketchName}' does not resolve to a sketch.");
        }

        var segments = new List<object>();
        var segs = sketch.GetSketchSegments() as object[] ?? Array.Empty<object>();
        foreach (var o in segs)
        {
            if (o is not ISketchSegment seg) { continue; }
            var typeName = ((swSketchSegments_e)seg.GetType()).ToString();
            segments.Add(new
            {
                type = typeName,
                name = seg.GetName(),
                construction = seg.ConstructionGeometry,
                lengthMm = Math.Round(seg.GetLength() * 1000, 4),
            });
        }

        var points = new List<object>();
        var pts = sketch.GetSketchPoints2() as object[] ?? Array.Empty<object>();
        foreach (var o in pts)
        {
            if (o is not ISketchPoint p) { continue; }
            points.Add(new
            {
                xMm = Math.Round(p.X * 1000, 4),
                yMm = Math.Round(p.Y * 1000, 4),
                zMm = Math.Round(p.Z * 1000, 4),
            });
        }

        return JsonSerializer.Serialize(new
        {
            sketch = (sketch as IFeature)?.Name,
            is3D = sketch.Is3D(),
            segmentCount = segments.Count,
            pointCount = points.Count,
            segments,
            points,
        });
    }

    // -------- Reference geometry --------

    [KernelFunction(nameof(GetReferenceGeometry))]
    [Description("List reference planes, axes, coordinate systems and " +
        "reference points in the active part or assembly. Returns JSON " +
        "{ planes[], axes[], points[], coordinateSystems[] } with each " +
        "feature's name and suppression state.")]
    public string GetReferenceGeometry()
    {
        var doc = ActiveSwDoc
            ?? throw new InvalidOperationException("No active SolidWorks document.");

        var planes = new List<object>();
        var axes = new List<object>();
        var points = new List<object>();
        var csys = new List<object>();

        for (var f = doc.FirstFeature() as IFeature; f is not null; f = f.GetNextFeature() as IFeature)
        {
            var t = f.GetTypeName2();
            var entry = new { name = f.Name, suppressed = f.IsSuppressed() };
            switch (t)
            {
                case "RefPlane":
                    planes.Add(entry);
                    break;
                case "RefAxis":
                    axes.Add(entry);
                    break;
                case "RefPoint":
                    points.Add(entry);
                    break;
                case "CoordSys":
                    csys.Add(entry);
                    break;
            }
        }

        return JsonSerializer.Serialize(new
        {
            doc = doc.GetTitle(),
            planes,
            axes,
            points,
            coordinateSystems = csys,
        });
    }

    // -------- Minimum radius on a face --------

    [KernelFunction(nameof(MeasureMinRadius))]
    [Description("Find the minimum radius of curvature of the currently " +
        "selected face (pre-select one face). Returns JSON " +
        "{ minRadiusMm, locationMm:[x,y,z] }. Useful for tool-access and " +
        "machining-feasibility checks. Planar faces report an infinite " +
        "radius and will fail.")]
    public string MeasureMinRadius()
    {
        var doc = ActiveSwDoc
            ?? throw new InvalidOperationException("No active SolidWorks document.");
        var sm = doc.SelectionManager as ISelectionMgr
            ?? throw new InvalidOperationException("SelectionManager unavailable.");
        if (sm.GetSelectedObjectCount2(-1) < 1)
        {
            throw new InvalidOperationException("Select one face first.");
        }
        var face = sm.GetSelectedObject6(1, -1) as IFace2
            ?? throw new InvalidOperationException("Selection 1 must be a face.");

        var surf = face.GetSurface() as ISurface
            ?? throw new InvalidOperationException("Face has no surface.");

        int count = 0;
        object radiusObj = null!;
        object locationObj = null!;
        object uvObj = null!;
        if (!surf.FindMinimumRadius(null, null, ref count, ref radiusObj,
                ref locationObj, ref uvObj) || count < 1)
        {
            throw new InvalidOperationException(
                "FindMinimumRadius returned no result. The face may be planar.");
        }

        var radii = radiusObj as double[] ?? Array.Empty<double>();
        var locs = locationObj as double[] ?? Array.Empty<double>();
        if (radii.Length == 0)
        {
            throw new InvalidOperationException("No radius values returned.");
        }

        return JsonSerializer.Serialize(new
        {
            minRadiusMm = Math.Round(radii[0] * 1000, 4),
            locationMm = locs.Length >= 3
                ? new[] { Math.Round(locs[0] * 1000, 4), Math.Round(locs[1] * 1000, 4), Math.Round(locs[2] * 1000, 4) }
                : Array.Empty<double>(),
            radiiCount = count,
        });
    }
}

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
}

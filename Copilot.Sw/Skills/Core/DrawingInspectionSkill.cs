using Microsoft.SemanticKernel;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Copilot.Sw.Skills.Core;

/// <summary>
/// Read-only QA checks for drawings (and the active document in general).
/// The headline aggregate is <see cref="InspectDrawing"/>.
/// </summary>
public sealed class DrawingInspectionSkill : SldWorksSkillContext
{
    /// <summary>Custom properties most engineering drawings need.</summary>
    private static readonly string[] DefaultRequiredProperties =
    {
        "PartNumber", "Description", "Material", "Finish",
        "Revision", "DrawnBy", "Date",
    };

    [KernelFunction(nameof(ListSheets))]
    [Description("List every sheet in the active drawing as JSON: name, " +
        "paper width/height in metres, view count.")]
    public string ListSheets()
    {
        var dwg = RequireDrawing();
        var names = dwg.GetSheetNames() as string[] ?? Array.Empty<string>();
        var rows = new List<object>();
        foreach (var n in names)
        {
            var sheet = (ISheet)dwg.get_Sheet(n);
            double w = 0, h = 0;
            sheet.GetSize(ref w, ref h);
            var views = sheet.GetViews() as object[] ?? Array.Empty<object>();
            rows.Add(new
            {
                name = n,
                widthMm = Math.Round(w * 1000, 2),
                heightMm = Math.Round(h * 1000, 2),
                viewCount = Math.Max(0, views.Length - 1),
            });
        }
        return JsonSerializer.Serialize(rows);
    }

    [KernelFunction(nameof(ListDrawingViews))]
    [Description("List every drawing view in the active drawing as JSON: " +
        "sheet, name, referenced model path/config, scale, position, " +
        "annotation count.")]
    public string ListDrawingViews()
    {
        var rows = CollectViews(RequireDrawing());
        return JsonSerializer.Serialize(rows);
    }

    [KernelFunction(nameof(ListFeatureTreeErrors))]
    [Description("Walk the feature tree of the active document and return " +
        "every feature with a non-zero error code as JSON. Useful for " +
        "spotting red/yellow features after a rebuild.")]
    public string ListFeatureTreeErrors()
    {
        var doc = ActiveSwDoc
            ?? throw new InvalidOperationException("No active SolidWorks document.");
        var rows = new List<object>();
        for (var feat = doc.FirstFeature() as IFeature; feat is not null; feat = feat.GetNextFeature() as IFeature)
        {
            CollectFeatureError(feat, rows);
        }
        return JsonSerializer.Serialize(rows);
    }

    [KernelFunction(nameof(CheckTitleBlock))]
    [Description("Check that all required custom properties exist on the " +
        "active document and have non-empty resolved values. " +
        "requiredProperties is comma-separated; pass empty for the default " +
        "engineering set (PartNumber, Description, Material, Finish, " +
        "Revision, DrawnBy, Date).")]
    public string CheckTitleBlock(string requiredProperties = "")
    {
        var doc = ActiveSwDoc
            ?? throw new InvalidOperationException("No active SolidWorks document.");
        var required = string.IsNullOrWhiteSpace(requiredProperties)
            ? DefaultRequiredProperties
            : requiredProperties.Split(',', ';').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();

        var missing = new List<string>();
        var empty = new List<string>();
        var present = new Dictionary<string, string>();

        var mgr = doc.Extension.get_CustomPropertyManager(string.Empty);
        foreach (var name in required)
        {
            string val = string.Empty, resolved = string.Empty;
            bool wasResolved = false;
            mgr.Get5(name, false, out val, out resolved, out wasResolved);
            if (string.IsNullOrEmpty(val))
            {
                missing.Add(name);
            }
            else if (string.IsNullOrWhiteSpace(resolved))
            {
                empty.Add(name);
            }
            else
            {
                present[name] = resolved;
            }
        }

        return JsonSerializer.Serialize(new
        {
            requiredCount = required.Length,
            missingCount = missing.Count,
            emptyCount = empty.Count,
            missing,
            empty,
            present,
        });
    }

    [KernelFunction(nameof(InspectDrawing))]
    [Description("Run a full accuracy/QA report on the active drawing. " +
        "Aggregates sheet/view inventory, view scale consistency, title " +
        "block completeness, feature-tree errors on each referenced " +
        "model, and missing-reference checks. Returns structured JSON " +
        "with a 'summary' plus per-category 'findings'.")]
    public string InspectDrawing(string requiredProperties = "")
    {
        var dwg = RequireDrawing();
        var doc = (IModelDoc2)dwg;

        var sheetRows = new List<object>();
        var viewRows = CollectViews(dwg);
        var titleBlock = JsonSerializer.Deserialize<JsonElement>(CheckTitleBlock(requiredProperties));

        var sheetNames = dwg.GetSheetNames() as string[] ?? Array.Empty<string>();
        foreach (var n in sheetNames)
        {
            var sheet = (ISheet)dwg.get_Sheet(n);
            double w = 0, h = 0;
            sheet.GetSize(ref w, ref h);
            sheetRows.Add(new { name = n, widthMm = Math.Round(w * 1000, 2), heightMm = Math.Round(h * 1000, 2) });
        }

        // View scale consistency per sheet.
        var scaleFindings = new List<string>();
        foreach (var sheetGroup in viewRows.GroupBy(v => v.SheetName))
        {
            var scales = sheetGroup.Where(v => v.Scale > 0).Select(v => v.Scale).Distinct().ToList();
            if (scales.Count > 1)
            {
                scaleFindings.Add(
                    $"Sheet '{sheetGroup.Key}' has {scales.Count} distinct view scales " +
                    $"({string.Join(", ", scales.Select(s => s.ToString("G4")))}). " +
                    "Section/detail views are expected to differ; verify the rest match.");
            }
        }

        // Missing referenced files.
        var missingRefs = viewRows
            .Where(v => !string.IsNullOrWhiteSpace(v.ModelPath) && !File.Exists(v.ModelPath))
            .Select(v => new { view = v.Name, sheet = v.SheetName, modelPath = v.ModelPath })
            .ToList();

        // Feature-tree errors on each unique referenced model.
        var modelErrors = new List<object>();
        foreach (var path in viewRows
                     .Select(v => v.ModelPath)
                     .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var model = Sw?.GetOpenDocumentByName(path) as IModelDoc2;
            if (model is null)
            {
                continue; // Model not currently loaded; skip silently.
            }
            var errs = new List<object>();
            for (var f = model.FirstFeature() as IFeature; f is not null; f = f.GetNextFeature() as IFeature)
            {
                CollectFeatureError(f, errs);
            }
            if (errs.Count > 0)
            {
                modelErrors.Add(new { modelPath = path, errors = errs });
            }
        }

        // Empty views (no annotations) - often forgotten section/detail labels.
        var emptyViewFindings = viewRows
            .Where(v => v.AnnotationCount == 0 && !v.IsSheet)
            .Select(v => new { view = v.Name, sheet = v.SheetName })
            .ToList();

        var summary = new
        {
            sheetCount = sheetRows.Count,
            viewCount = viewRows.Count(v => !v.IsSheet),
            titleBlockMissing = titleBlock.GetProperty("missingCount").GetInt32(),
            scaleInconsistencies = scaleFindings.Count,
            missingReferenceCount = missingRefs.Count,
            modelsWithErrors = modelErrors.Count,
            viewsWithoutAnnotations = emptyViewFindings.Count,
        };

        return JsonSerializer.Serialize(new
        {
            drawing = doc.GetTitle(),
            summary,
            findings = new
            {
                sheets = sheetRows,
                views = viewRows,
                titleBlock,
                scaleInconsistencies = scaleFindings,
                missingReferences = missingRefs,
                modelErrors,
                viewsWithoutAnnotations = emptyViewFindings,
            },
        }, new JsonSerializerOptions { WriteIndented = false });
    }

    // --- helpers ---

    private IDrawingDoc RequireDrawing()
    {
        var doc = ActiveSwDoc
            ?? throw new InvalidOperationException("No active SolidWorks document.");
        if (doc is not IDrawingDoc dwg
            || (swDocumentTypes_e)doc.GetType() != swDocumentTypes_e.swDocDRAWING)
        {
            throw new InvalidOperationException(
                "Drawing inspection requires an active drawing document.");
        }
        return dwg;
    }

    private sealed class ViewRow
    {
        public string SheetName { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public bool IsSheet { get; set; }
        public string ModelPath { get; set; } = string.Empty;
        public string Config { get; set; } = string.Empty;
        public double Scale { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public int AnnotationCount { get; set; }
    }

    private static List<ViewRow> CollectViews(IDrawingDoc dwg)
    {
        var rows = new List<ViewRow>();
        var sheetNames = dwg.GetSheetNames() as string[] ?? Array.Empty<string>();
        foreach (var sheetName in sheetNames)
        {
            var sheet = (ISheet)dwg.get_Sheet(sheetName);
            var views = sheet.GetViews() as object[] ?? Array.Empty<object>();
            for (int i = 0; i < views.Length; i++)
            {
                if (views[i] is not IView v)
                {
                    continue;
                }
                double scale = 0;
                try
                {
                    scale = v.ScaleDecimal;
                }
                catch
                {
                    // Sheet "view" doesn't have a scale.
                }
                var pos = v.Position as double[] ?? new double[2];
                var modelDoc = v.ReferencedDocument as IModelDoc2;
                rows.Add(new ViewRow
                {
                    SheetName = sheetName,
                    Name = v.GetName2() ?? string.Empty,
                    IsSheet = i == 0,
                    ModelPath = modelDoc?.GetPathName() ?? string.Empty,
                    Config = v.ReferencedConfiguration ?? string.Empty,
                    Scale = scale,
                    X = pos.Length > 0 ? pos[0] : 0,
                    Y = pos.Length > 1 ? pos[1] : 0,
                    AnnotationCount = CountAnnotations(v),
                });
            }
        }
        return rows;
    }

    private static int CountAnnotations(IView v)
    {
        int n = 0;
        var ann = v.GetFirstAnnotation3() as IAnnotation;
        while (ann is not null)
        {
            n++;
            ann = ann.GetNext3() as IAnnotation;
            if (n > 5000)
            {
                break; // safety
            }
        }
        return n;
    }

    private static void CollectFeatureError(IFeature feat, List<object> rows)
    {
        var code = feat.GetErrorCode2(out bool isWarning);
        if (code != 0)
        {
            rows.Add(new
            {
                name = feat.Name,
                type = feat.GetTypeName2(),
                errorCode = code,
                isWarning,
            });
        }
        for (var sub = feat.GetFirstSubFeature() as IFeature; sub is not null; sub = sub.GetNextSubFeature() as IFeature)
        {
            CollectFeatureError(sub, rows);
        }
    }
}

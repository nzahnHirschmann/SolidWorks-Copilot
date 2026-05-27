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

    [KernelFunction(nameof(CheckMissingDimensions))]
    [Description("For every view on the active drawing, compare the count " +
        "of dimensions to the count of visible edges and flag views with " +
        "zero dimensions, or a dim-to-edge ratio below 'minRatio' " +
        "(default 0.3). Section / detail views often legitimately have " +
        "few dimensions; treat findings as guidance, not as hard errors.")]
    public string CheckMissingDimensions(double minRatio = 0.3)
    {
        var dwg = RequireDrawing();
        var findings = new List<object>();
        int viewCount = 0;
        foreach (var (sheetName, view) in EnumerateViews(dwg))
        {
            viewCount++;
            int dims = view.GetDisplayDimensionCount();
            int edges = view.GetVisibleEntityCount2(null, (int)swSelectType_e.swSelEDGES);
            double ratio = edges > 0 ? (double)dims / edges : 0;
            if (dims == 0 && edges > 0)
            {
                findings.Add(new
                {
                    view = view.GetName2(),
                    sheet = sheetName,
                    dimensions = dims,
                    visibleEdges = edges,
                    ratio,
                    severity = "missing",
                });
            }
            else if (edges > 0 && ratio < minRatio)
            {
                findings.Add(new
                {
                    view = view.GetName2(),
                    sheet = sheetName,
                    dimensions = dims,
                    visibleEdges = edges,
                    ratio = Math.Round(ratio, 3),
                    severity = "sparse",
                });
            }
        }
        return JsonSerializer.Serialize(new { viewCount, threshold = minRatio, findings });
    }

    [KernelFunction(nameof(CheckToleranceSanity))]
    [Description("Walk every display dimension on the active drawing and " +
        "report tolerance coverage: counts by tolerance type (NONE, " +
        "BASIC, BILAT, LIMIT, SYMMETRIC, MIN, MAX, FIT, …) plus a " +
        "'untoleranced' list of dimensions that carry no tolerance.")]
    public string CheckToleranceSanity()
    {
        var dwg = RequireDrawing();
        var counts = new Dictionary<string, int>();
        var untoleranced = new List<object>();
        int total = 0;
        foreach (var (sheetName, view) in EnumerateViews(dwg))
        {
            int n = view.GetDisplayDimensionCount();
            var dds = view.GetDisplayDimensions() as object[] ?? Array.Empty<object>();
            for (int i = 0; i < dds.Length; i++)
            {
                var dd = dds[i] as IDisplayDimension;
                var dim = dd?.GetDimension() as IDimension;
                if (dim is null)
                {
                    continue;
                }
                total++;
                var tol = (swTolType_e)dim.GetToleranceType();
                var key = tol.ToString();
                counts[key] = counts.TryGetValue(key, out var c) ? c + 1 : 1;
                if (tol == swTolType_e.swTolNONE)
                {
                    untoleranced.Add(new
                    {
                        view = view.GetName2(),
                        sheet = sheetName,
                        name = dim.FullName,
                        valueMm = Math.Round(dim.GetSystemValue2(string.Empty) * 1000, 4),
                    });
                }
            }
        }
        return JsonSerializer.Serialize(new
        {
            totalDimensions = total,
            byToleranceType = counts,
            untolerancedCount = untoleranced.Count,
            untoleranced,
        });
    }

    [KernelFunction(nameof(CheckGdtConsistency))]
    [Description("Cross-check GD&T on the active drawing: every datum " +
        "letter referenced inside a control frame must have a matching " +
        "datum-feature symbol. Returns JSON with the set of defined " +
        "datum letters, the set of referenced letters, and any " +
        "referenced letters that are missing.")]
    public string CheckGdtConsistency()
    {
        var dwg = RequireDrawing();
        var definedDatums = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var referencedDatums = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int gtolCount = 0;
        int datumTagCount = 0;

        foreach (var (_, view) in EnumerateViews(dwg))
        {
            var ann = view.GetFirstAnnotation3() as IAnnotation;
            int safety = 0;
            while (ann is not null && safety++ < 10000)
            {
                var t = (swAnnotationType_e)ann.GetType();
                if (t == swAnnotationType_e.swDatumTag
                    && ann.GetSpecificAnnotation() is IDatumTag dt)
                {
                    datumTagCount++;
                    var lbl = dt.GetLabel();
                    if (!string.IsNullOrWhiteSpace(lbl))
                    {
                        definedDatums.Add(lbl.Trim());
                    }
                }
                else if (t == swAnnotationType_e.swGTol
                    && ann.GetSpecificAnnotation() is IGtol gtol)
                {
                    gtolCount++;
                    var frames = gtol.GetFrameCount();
                    for (short f = 0; f < frames; f++)
                    {
                        if (gtol.GetFrameValues(f) is string[] vals && vals.Length >= 5)
                        {
                            for (int k = 2; k <= 4; k++)
                            {
                                var d = vals[k];
                                if (!string.IsNullOrWhiteSpace(d))
                                {
                                    referencedDatums.Add(d.Trim());
                                }
                            }
                        }
                    }
                }
                ann = ann.GetNext3() as IAnnotation;
            }
        }

        var missing = referencedDatums.Except(definedDatums, StringComparer.OrdinalIgnoreCase).ToList();
        return JsonSerializer.Serialize(new
        {
            datumTagCount,
            gtolCount,
            definedDatums = definedDatums.OrderBy(s => s).ToList(),
            referencedDatums = referencedDatums.OrderBy(s => s).ToList(),
            missingDatumDefinitions = missing,
        });
    }

    [KernelFunction(nameof(CheckBomVsAssembly))]
    [Description("Compare the first BoM table on the active drawing to " +
        "the live component list of its linked assembly (must be open). " +
        "Returns JSON with extra-in-BoM, missing-from-BoM, and quantity " +
        "mismatches keyed by file name. The linked assembly must " +
        "currently be open in SolidWorks for the comparison to run.")]
    public string CheckBomVsAssembly()
    {
        var dwg = RequireDrawing();
        var boms = EnumerateBomTables(dwg).ToList();
        if (boms.Count == 0)
        {
            throw new InvalidOperationException(
                "No BoM table found on the active drawing. Insert a BoM first.");
        }
        var bom = boms[0];
        var bomFeature = bom.BomFeature;
        var asmPath = bomFeature?.GetReferencedModelName() ?? string.Empty;
        var table = (ITableAnnotation)bom;

        var bomRows = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < table.RowCount; i++)
        {
            var comps = bom.GetComponents2(i, string.Empty) as object[] ?? Array.Empty<object>();
            if (comps.Length == 0)
            {
                continue;
            }
            string? path = null;
            foreach (var co in comps)
            {
                if (co is IComponent2 c)
                {
                    path = c.GetPathName();
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        break;
                    }
                }
            }
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }
            var key = Path.GetFileNameWithoutExtension(path);
            bomRows[key] = (bomRows.TryGetValue(key, out var v) ? v : 0) + comps.Length;
        }

        var asmRows = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var asm = string.IsNullOrEmpty(asmPath) ? null : Sw?.GetOpenDocumentByName(asmPath) as IAssemblyDoc;
        if (asm is not null)
        {
            var comps = asm.GetComponents(false) as object[] ?? Array.Empty<object>();
            foreach (var co in comps)
            {
                if (co is not IComponent2 c)
                {
                    continue;
                }
                if (c.IsSuppressed())
                {
                    continue;
                }
                var p = c.GetPathName();
                if (string.IsNullOrWhiteSpace(p))
                {
                    continue;
                }
                var key = Path.GetFileNameWithoutExtension(p);
                asmRows[key] = (asmRows.TryGetValue(key, out var v) ? v : 0) + 1;
            }
        }

        var extraInBom = bomRows
            .Where(kv => !asmRows.ContainsKey(kv.Key))
            .Select(kv => new { name = kv.Key, bomQty = kv.Value })
            .ToList();
        var missingFromBom = asmRows
            .Where(kv => !bomRows.ContainsKey(kv.Key))
            .Select(kv => new { name = kv.Key, asmQty = kv.Value })
            .ToList();
        var qtyMismatch = bomRows
            .Where(kv => asmRows.TryGetValue(kv.Key, out var av) && av != kv.Value)
            .Select(kv => new { name = kv.Key, bomQty = kv.Value, asmQty = asmRows[kv.Key] })
            .ToList();

        return JsonSerializer.Serialize(new
        {
            bomTablesFound = boms.Count,
            assembly = asmPath,
            assemblyLoaded = asm is not null,
            bomUniqueParts = bomRows.Count,
            assemblyUniqueParts = asmRows.Count,
            extraInBom,
            missingFromBom,
            qtyMismatch,
        });
    }

    [KernelFunction(nameof(CheckSpelling))]
    [Description("Collect every textual string from the active document so " +
        "the LLM can spell-check them: notes, GTol text, weld-symbol " +
        "text, dimension overrides, sheet titles, and file-level custom " +
        "property values. Returns JSON { drawing, items[] } where each " +
        "item is { source, location, text }. SolidWorks' native " +
        "CheckSpelling opens an interactive dialog, so this function " +
        "delegates the actual spell-checking to the model.")]
    public string CheckSpelling()
    {
        var doc = ActiveSwDoc
            ?? throw new InvalidOperationException("No active SolidWorks document.");
        var items = new List<object>();

        // Custom property values (title-block source) — file level only.
        var mgr = doc.Extension.get_CustomPropertyManager(string.Empty);
        var names = mgr.GetNames() as string[] ?? Array.Empty<string>();
        foreach (var n in names)
        {
            mgr.Get5(n, false, out _, out string resolved, out _);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                items.Add(new { source = "customProperty", location = n, text = resolved });
            }
        }

        // Annotation text — walk every annotation, including per-view ones on a drawing.
        if (doc is IDrawingDoc dwg)
        {
            var sheetNames = dwg.GetSheetNames() as string[] ?? Array.Empty<string>();
            foreach (var sn in sheetNames)
            {
                items.Add(new { source = "sheetName", location = sn, text = sn });
                var sheet = (ISheet)dwg.get_Sheet(sn);
                var views = sheet.GetViews() as object[] ?? Array.Empty<object>();
                foreach (var vo in views)
                {
                    if (vo is not IView v) { continue; }
                    CollectAnnotationText(v.GetFirstAnnotation3() as IAnnotation,
                        $"sheet '{sn}' / view '{v.GetName2()}'", items);
                }
            }
        }
        else
        {
            CollectAnnotationText(doc.GetFirstAnnotation2() as IAnnotation,
                "model", items);
        }

        return JsonSerializer.Serialize(new
        {
            drawing = doc.GetTitle(),
            itemCount = items.Count,
            items,
        });
    }

    [KernelFunction(nameof(RunDesignChecker))]
    [Description("Run the SOLIDWORKS Design Checker addin against the " +
        "active document using a .swstd standards file and return its " +
        "results as JSON. Requires the Design Checker addin to be " +
        "available on the host. This is a best-effort wrapper: the " +
        "addin's COM surface is undocumented and varies by SOLIDWORKS " +
        "version, so on failure the function reports what it tried and " +
        "suggests InspectDrawing as a fallback.")]
    public string RunDesignChecker(string standardsFile)
    {
        if (string.IsNullOrWhiteSpace(standardsFile))
        {
            throw new ArgumentException("standardsFile is required (.swstd).",
                nameof(standardsFile));
        }
        if (!File.Exists(standardsFile))
        {
            throw new InvalidOperationException(
                $"Standards file not found: {standardsFile}");
        }
        var sw = Sw
            ?? throw new InvalidOperationException("SolidWorks is not running.");
        var doc = ActiveSwDoc
            ?? throw new InvalidOperationException("No active SolidWorks document.");

        const string clsid = "{59F38FA7-1FAC-4ED6-A5B9-5D1B7DD0FD4D}";
        var tried = new List<string>();

        object? addin = sw.GetAddInObject(clsid);
        tried.Add($"GetAddInObject({clsid}) -> {(addin is null ? "null" : "object")}");
        if (addin is null)
        {
            int loadResult = sw.LoadAddIn(clsid);
            tried.Add($"LoadAddIn({clsid}) -> {loadResult}");
            addin = sw.GetAddInObject(clsid);
            tried.Add($"GetAddInObject(after-load) -> {(addin is null ? "null" : "object")}");
        }
        if (addin is null)
        {
            throw new InvalidOperationException(
                "Could not obtain the Design Checker addin object. " +
                "Verify SwDesignCheck.dll is installed and registered. " +
                "Fallback: InspectDrawing aggregates equivalent QA checks. " +
                "Tried: " + string.Join("; ", tried));
        }

        // Try the documented batch-mode entry points in order; method names
        // differ across SolidWorks versions, so use reflection and swallow
        // missing-member exceptions for the ones that don't exist.
        var addinType = addin.GetType();
        var attempts = new (string name, Func<object?> call)[]
        {
            ("CheckActiveDocument(standardsFile)",       () => addinType.InvokeMember("CheckActiveDocument",       System.Reflection.BindingFlags.InvokeMethod, null, addin, new object[] { standardsFile })),
            ("RunStandardsCheck(doc, standardsFile)",    () => addinType.InvokeMember("RunStandardsCheck",        System.Reflection.BindingFlags.InvokeMethod, null, addin, new object[] { doc, standardsFile })),
            ("OpenAndCheckDocument(standardsFile, doc)", () => addinType.InvokeMember("OpenAndCheckDocument",     System.Reflection.BindingFlags.InvokeMethod, null, addin, new object[] { standardsFile, doc })),
            ("CheckDocument(doc, standardsFile)",        () => addinType.InvokeMember("CheckDocument",            System.Reflection.BindingFlags.InvokeMethod, null, addin, new object[] { doc, standardsFile })),
        };
        foreach (var (name, call) in attempts)
        {
            try
            {
                var result = call();
                return JsonSerializer.Serialize(new
                {
                    addinClsid = clsid,
                    method = name,
                    standardsFile,
                    raw = result?.ToString(),
                });
            }
            catch (Exception ex)
            {
                tried.Add($"{name} -> {ex.GetType().Name}: {ex.Message}");
            }
        }
        throw new InvalidOperationException(
            "Design Checker addin loaded but no known entry point accepted the call. " +
            "Fallback: InspectDrawing aggregates equivalent QA checks. " +
            "Tried: " + string.Join("; ", tried));
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
                break;
            }
        }
        return n;
    }

    private static IEnumerable<(string SheetName, IView View)> EnumerateViews(IDrawingDoc dwg)
    {
        var sheetNames = dwg.GetSheetNames() as string[] ?? Array.Empty<string>();
        foreach (var sn in sheetNames)
        {
            var sheet = (ISheet)dwg.get_Sheet(sn);
            var views = sheet.GetViews() as object[] ?? Array.Empty<object>();
            for (int i = 0; i < views.Length; i++)
            {
                if (i == 0)
                {
                    continue; // first entry is the sheet itself
                }
                if (views[i] is IView v)
                {
                    yield return (sn, v);
                }
            }
        }
    }

    private static IEnumerable<IBomTableAnnotation> EnumerateBomTables(IDrawingDoc dwg)
    {
        foreach (var (_, v) in EnumerateViews(dwg))
        {
            var ann = v.GetFirstAnnotation3() as IAnnotation;
            int safety = 0;
            while (ann is not null && safety++ < 5000)
            {
                if ((swAnnotationType_e)ann.GetType() == swAnnotationType_e.swTableAnnotation
                    && ann.GetSpecificAnnotation() is IBomTableAnnotation bom)
                {
                    yield return bom;
                }
                ann = ann.GetNext3() as IAnnotation;
            }
        }
    }

    private static void CollectAnnotationText(IAnnotation? first, string location, List<object> items)
    {
        var ann = first;
        int safety = 0;
        while (ann is not null && safety++ < 10000)
        {
            var t = (swAnnotationType_e)ann.GetType();
            var spec = ann.GetSpecificAnnotation();
            switch (t)
            {
                case swAnnotationType_e.swNote when spec is INote note:
                    Add(note.GetText(), "note");
                    break;
                case swAnnotationType_e.swDisplayDimension when spec is IDisplayDimension dd:
                    Add(dd.GetText((int)swDimensionTextParts_e.swDimensionTextPrefix), "dim.prefix");
                    Add(dd.GetText((int)swDimensionTextParts_e.swDimensionTextSuffix), "dim.suffix");
                    Add(dd.GetText((int)swDimensionTextParts_e.swDimensionTextCalloutAbove), "dim.above");
                    Add(dd.GetText((int)swDimensionTextParts_e.swDimensionTextCalloutBelow), "dim.below");
                    break;
                case swAnnotationType_e.swWeldSymbol when spec is IWeldSymbol ws:
                    Add(ws.GetTextAtIndex(0), "weld");
                    break;
                case swAnnotationType_e.swGTol when spec is IGtol gtol:
                    {
                        int below = gtol.GetBelowFrameTextLineCount();
                        for (int i = 0; i < below; i++)
                        {
                            Add(gtol.GetBelowFrameTextAt(i), "gtol.belowFrame");
                        }
                    }
                    break;
            }
            ann = ann.GetNext3() as IAnnotation;
        }

        void Add(string? text, string kind)
        {
            if (string.IsNullOrWhiteSpace(text)) { return; }
            items.Add(new { source = kind, location, text });
        }
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

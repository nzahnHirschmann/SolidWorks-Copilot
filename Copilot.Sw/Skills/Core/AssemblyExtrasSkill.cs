using Copilot.Sw.Skills;
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
/// Remaining P2 assembly operations: per-component move/delete, mate
/// listing, BoM, and mate-error evaluation. Read-side bits are useful
/// for the model to reason about an existing assembly.
/// </summary>
public sealed class AssemblyExtrasSkill : SldWorksSkillContext
{
    // -------- Component move / delete --------

    [KernelFunction(nameof(MoveComponent))]
    [Description("Translate the named component by (dx, dy, dz) in the " +
        "document units. Pass 'componentName' as shown by ListComponents " +
        "(e.g. 'Bolt-1@MyAssembly').")]
    public string MoveComponent(string componentName, double dx, double dy, double dz, string? unit = null)
    {
        if (string.IsNullOrWhiteSpace(componentName))
        {
            throw new ArgumentException("componentName is required.", nameof(componentName));
        }
        var assy = RequireAssembly();
        var doc = (IModelDoc2)assy;
        var comp = assy.GetComponentByName(componentName)
            ?? throw new InvalidOperationException(
                $"Component '{componentName}' not found in the active assembly.");

        var txM = SwUnits.ToMeters(dx, unit, doc);
        var tyM = SwUnits.ToMeters(dy, unit, doc);
        var tzM = SwUnits.ToMeters(dz, unit, doc);

        var mu = (IMathUtility)Sw!.GetMathUtility();
        var data = new double[] { 1,0,0, 0,1,0, 0,0,1, txM, tyM, tzM, 1, 0, 0, 0 };
        var delta = mu.CreateTransform(data) as MathTransform
            ?? throw new InvalidOperationException("Failed to create math transform.");

        comp.Transform2 = comp.Transform2.Multiply(delta) as MathTransform
            ?? throw new InvalidOperationException("Transform multiplication failed.");
        return $"Moved '{componentName}' by ({dx}, {dy}, {dz}) {SwUnits.GetDocumentLengthUnit(doc)}.";
    }

    [KernelFunction(nameof(DeleteComponent))]
    [Description("Delete the named component from the active assembly. " +
        "Pass 'componentName' as shown by ListComponents.")]
    public string DeleteComponent(string componentName)
    {
        if (string.IsNullOrWhiteSpace(componentName))
        {
            throw new ArgumentException("componentName is required.", nameof(componentName));
        }
        var assy = RequireAssembly();
        var doc = (IModelDoc2)assy;
        var comp = assy.GetComponentByName(componentName)
            ?? throw new InvalidOperationException(
                $"Component '{componentName}' not found.");

        doc.ClearSelection2(true);
        if (!comp.Select4(false, null, false))
        {
            throw new InvalidOperationException($"Could not select '{componentName}'.");
        }
        if (!assy.DeleteSelections(0))
        {
            throw new InvalidOperationException($"DeleteSelections failed for '{componentName}'.");
        }
        return $"Deleted '{componentName}'.";
    }

    // -------- Mate listing & QA --------

    [KernelFunction(nameof(ListMates))]
    [Description("Return JSON list of every mate in the active assembly: " +
        "name, type, alignment, error status, and the involved component " +
        "names. Useful for checking what's wired up.")]
    public string ListMates()
    {
        var assy = RequireAssembly();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<object>();

        foreach (var comp in EnumerateComponents(assy, topLevelOnly: false))
        {
            var mates = comp.GetMates() as object[] ?? Array.Empty<object>();
            foreach (var m in mates)
            {
                if (m is not IFeature mf)
                {
                    continue;
                }
                if (!seen.Add(mf.Name))
                {
                    continue;
                }
                rows.Add(BuildMateRow(mf));
            }
        }
        return JsonSerializer.Serialize(new { count = rows.Count, mates = rows });
    }

    [KernelFunction(nameof(EvaluateMateErrors))]
    [Description("Walk every mate in the active assembly and report ones " +
        "with non-zero error codes (warnings or failures). Returns JSON " +
        "{ count, errors: [{ name, type, errorCode, isWarning }] }.")]
    public string EvaluateMateErrors()
    {
        var assy = RequireAssembly();
        var doc = (IModelDoc2)assy;
        var errors = new List<object>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var f = doc.FirstFeature() as IFeature; f is not null; f = f.GetNextFeature() as IFeature)
        {
            CollectMateErrors(f, errors, seen);
        }
        return JsonSerializer.Serialize(new { count = errors.Count, errors });
    }

    private static void CollectMateErrors(IFeature feat, List<object> errors, HashSet<string> seen)
    {
        if (feat.GetSpecificFeature2() is IMate2)
        {
            if (seen.Add(feat.Name))
            {
                var code = feat.GetErrorCode2(out bool isWarning);
                if (code != 0)
                {
                    errors.Add(new
                    {
                        name = feat.Name,
                        type = feat.GetTypeName2(),
                        errorCode = code,
                        isWarning,
                    });
                }
            }
        }
        for (var sub = feat.GetFirstSubFeature() as IFeature;
             sub is not null;
             sub = sub.GetNextSubFeature() as IFeature)
        {
            CollectMateErrors(sub, errors, seen);
        }
    }

    private static object BuildMateRow(IFeature mf)
    {
        var components = new List<string>();
        int typeId = -1;
        int alignId = -1;
        if (mf.GetSpecificFeature2() is IMate2 mate)
        {
            typeId = mate.Type;
            var entCount = mate.GetMateEntityCount();
            for (int i = 0; i < entCount; i++)
            {
                var ent = mate.MateEntity(i);
                if (ent?.ReferenceComponent is IComponent2 c)
                {
                    components.Add(c.Name2);
                }
            }
        }
        var code = mf.GetErrorCode2(out bool isWarning);
        return new
        {
            name = mf.Name,
            type = typeId >= 0 ? ((swMateType_e)typeId).ToString() : mf.GetTypeName2(),
            components,
            errorCode = code,
            isWarning,
        };
    }

    // -------- Bill of Materials --------

    [KernelFunction(nameof(GetBoM))]
    [Description("Return a flat bill of materials for the active assembly: " +
        "one row per unique (file, configuration) pair with a quantity. " +
        "Pass topLevelOnly=true for a one-level BoM, false (default) for a " +
        "fully-flattened BoM across the whole tree.")]
    public string GetBoM(bool topLevelOnly = false)
    {
        var assy = RequireAssembly();
        var groups = new Dictionary<string, (string name, string path, string config, int qty)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var comp in EnumerateComponents(assy, topLevelOnly))
        {
            if (comp.IsSuppressed())
            {
                continue;
            }
            var path = comp.GetPathName() ?? string.Empty;
            var config = comp.ReferencedConfiguration ?? string.Empty;
            var key = $"{path}||{config}";
            if (groups.TryGetValue(key, out var existing))
            {
                groups[key] = (existing.name, existing.path, existing.config, existing.qty + 1);
            }
            else
            {
                groups[key] = (Path.GetFileNameWithoutExtension(path), path, config, 1);
            }
        }

        var rows = groups.Values
            .OrderByDescending(g => g.qty)
            .ThenBy(g => g.name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new
            {
                name = g.name,
                path = g.path,
                config = g.config,
                quantity = g.qty,
            })
            .ToList();
        return JsonSerializer.Serialize(new
        {
            assembly = ((IModelDoc2)assy).GetTitle(),
            topLevelOnly,
            uniqueParts = rows.Count,
            totalInstances = rows.Sum(r => r.quantity),
            items = rows,
        });
    }

    // -------- Replace component --------

    [KernelFunction(nameof(ReplaceComponent))]
    [Description("Replace the currently-selected component with the part " +
        "or assembly at 'newPath'. Pre-select one component instance " +
        "first. 'replaceAllInstances' replaces every occurrence of the " +
        "same model; 'reAttachMates' tries to re-bind existing mates to " +
        "the new geometry. 'configurationName' picks a non-default config.")]
    public string ReplaceComponent(string newPath, bool replaceAllInstances = true, bool reAttachMates = true, string configurationName = "")
    {
        if (string.IsNullOrWhiteSpace(newPath))
        {
            throw new ArgumentException("newPath is required.", nameof(newPath));
        }
        if (!File.Exists(newPath))
        {
            throw new InvalidOperationException($"File not found: {newPath}");
        }
        var assy = RequireAssembly();
        var doc = (IModelDoc2)assy;
        var sm = doc.SelectionManager as ISelectionMgr
            ?? throw new InvalidOperationException("SelectionManager unavailable.");
        if (sm.GetSelectedObjectCount2(-1) < 1)
        {
            throw new InvalidOperationException(
                "Select the component instance to replace first.");
        }

        if (!assy.ReplaceComponents2(newPath, configurationName ?? string.Empty,
                replaceAllInstances, 0, reAttachMates))
        {
            throw new InvalidOperationException(
                $"ReplaceComponents2 failed for '{newPath}'.");
        }
        return $"Replaced component with '{Path.GetFileName(newPath)}' " +
            $"(allInstances={replaceAllInstances}, reAttachMates={reAttachMates}).";
    }

    // -------- Exploded views --------

    [KernelFunction(nameof(ListExplodedViews))]
    [Description("Return JSON list of exploded view names defined on the " +
        "active assembly's current configuration.")]
    public string ListExplodedViews()
    {
        var assy = RequireAssembly();
        var names = assy.GetExplodedViewNames2(string.Empty) as string[] ?? Array.Empty<string>();
        return JsonSerializer.Serialize(new
        {
            assembly = ((IModelDoc2)assy).GetTitle(),
            count = names.Length,
            names,
        });
    }

    [KernelFunction(nameof(ShowExplodedView))]
    [Description("Show or collapse a named exploded view on the active " +
        "assembly. Pass 'show=false' to collapse. Use ListExplodedViews " +
        "to discover valid names.")]
    public string ShowExplodedView(string name, bool show = true)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("name is required.", nameof(name));
        }
        var assy = RequireAssembly();
        if (!assy.ShowExploded2(show, name))
        {
            throw new InvalidOperationException(
                $"ShowExploded2 failed for '{name}'. Verify the view exists in the active configuration.");
        }
        return show ? $"Exploded '{name}'." : $"Collapsed '{name}'.";
    }

    [KernelFunction(nameof(CreateExplodedView))]
    [Description("Author a new exploded view on the active configuration. " +
        "'stepsJson' is a JSON array of translational explode steps: " +
        "`[{ \"components\": [\"Bolt-1@Asm\"], \"axis\": \"X|Y|Z\", \"distanceMm\": 50, \"reverse\": false }, ...]`. " +
        "Each step selects the listed components and translates them along the chosen global axis by " +
        "the requested distance. SolidWorks auto-names the resulting view (typically ExplViewN); call " +
        "ListExplodedViews afterwards to discover the name.")]
    public string CreateExplodedView(string stepsJson)
    {
        if (string.IsNullOrWhiteSpace(stepsJson))
        {
            throw new ArgumentException("stepsJson is required.", nameof(stepsJson));
        }

        ExplodeStepInput[]? steps;
        try
        {
            steps = JsonSerializer.Deserialize<ExplodeStepInput[]>(stepsJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            throw new ArgumentException(
                $"stepsJson is not valid JSON: {ex.Message}", nameof(stepsJson));
        }
        if (steps is null || steps.Length == 0)
        {
            throw new ArgumentException("stepsJson must contain at least one step.", nameof(stepsJson));
        }

        var assy = RequireAssembly();
        var doc = (IModelDoc2)assy;
        var config = doc.ConfigurationManager?.ActiveConfiguration
            ?? throw new InvalidOperationException("No active configuration on the assembly.");

        var before = (assy.GetExplodedViewNames2(string.Empty) as string[] ?? Array.Empty<string>())
            .ToHashSet(StringComparer.Ordinal);

        var added = 0;
        for (var i = 0; i < steps.Length; i++)
        {
            var step = steps[i];
            if (step is null || step.Components is null || step.Components.Length == 0)
            {
                throw new ArgumentException(
                    $"Step {i + 1}: at least one component name is required.");
            }

            doc.ClearSelection2(true);
            foreach (var name in step.Components)
            {
                var comp = assy.GetComponentByName(name)
                    ?? throw new InvalidOperationException(
                        $"Step {i + 1}: component '{name}' not found.");
                if (!comp.Select4(true, null, false))
                {
                    throw new InvalidOperationException(
                        $"Step {i + 1}: could not select '{name}'.");
                }
            }

            var axisIdx = step.Axis?.Trim().ToUpperInvariant() switch
            {
                "X" or "" or null => (int)swExplodeDirectionIndex_e.swExplodeDirectionIndex_XAxis,
                "Y" => (int)swExplodeDirectionIndex_e.swExplodeDirectionIndex_YAxis,
                "Z" => (int)swExplodeDirectionIndex_e.swExplodeDirectionIndex_ZAxis,
                _ => throw new ArgumentException(
                    $"Step {i + 1}: axis must be X, Y or Z (got '{step.Axis}')."),
            };

            var distMeters = step.DistanceMm / 1000.0;
            // AddExplodeStep2(distance, translationAxisType, reverseTranslation,
            //                 rotationAngle, rotationAxisType, reverseRotation,
            //                 autoSpaceComponents, divergeFromAxis, out errors)
            var result = config.AddExplodeStep2(
                distMeters,
                axisIdx,
                step.Reverse,
                0.0,
                axisIdx,
                false,
                false,
                false,
                out int errors);
            if (result is null || errors != 0)
            {
                throw new InvalidOperationException(
                    $"Step {i + 1}: AddExplodeStep2 failed (errors={errors}).");
            }
            added++;
        }

        var after = assy.GetExplodedViewNames2(string.Empty) as string[] ?? Array.Empty<string>();
        var created = after.FirstOrDefault(n => !before.Contains(n));
        return JsonSerializer.Serialize(new
        {
            stepsAdded = added,
            createdView = created,
            allViews = after,
        });
    }

    private sealed class ExplodeStepInput
    {
        public string[] Components { get; set; } = Array.Empty<string>();
        public string? Axis { get; set; }
        public double DistanceMm { get; set; }
        public bool Reverse { get; set; }
    }

    // -------- Helpers --------

    private IAssemblyDoc RequireAssembly()
    {
        var doc = ActiveSwDoc
            ?? throw new InvalidOperationException("No active SolidWorks document.");
        if (doc is not IAssemblyDoc assy
            || (swDocumentTypes_e)doc.GetType() != swDocumentTypes_e.swDocASSEMBLY)
        {
            throw new InvalidOperationException("An active assembly document is required.");
        }
        return assy;
    }

    private static IEnumerable<IComponent2> EnumerateComponents(IAssemblyDoc assy, bool topLevelOnly)
    {
        var raw = assy.GetComponents(topLevelOnly) as object[] ?? Array.Empty<object>();
        foreach (var o in raw)
        {
            if (o is IComponent2 c)
            {
                yield return c;
            }
        }
    }
}

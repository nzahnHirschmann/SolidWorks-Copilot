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

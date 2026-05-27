using Microsoft.SemanticKernel;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Copilot.Sw.Skills.TemplatesSkill;

/// <summary>
/// Named, reusable procedures the model can invoke as a single tool call
/// instead of authoring a fresh plan from scratch — the "templates /
/// macros library" feature in §5 of the roadmap.
///
/// Templates are stored in a user-editable JSON file at
/// <c>%APPDATA%\Copilot.Sw\templates.json</c>. If the file doesn't exist
/// the first call materialises it with a set of sensible defaults. The
/// model uses <see cref="ListTemplates"/> to discover what's available
/// and <see cref="GetTemplate"/> to retrieve the procedure body, which
/// it then executes by calling the relevant SolidWorks KernelFunctions.
/// </summary>
public sealed class TemplatesSkillContext : SldWorksSkillContext
{
    private static readonly string DefaultStorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Copilot.Sw",
        "templates.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [KernelFunction, Description(
        "Lists every named template (reusable procedure) available in the user's templates library. " +
        "Returns a markdown bullet list of `name — description`.")]
    public string ListTemplates()
    {
        var store = Load();
        if (store.Templates.Count == 0)
        {
            return "(no templates)";
        }
        return string.Join("\n",
            store.Templates
                .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .Select(t => $"- **{t.Name}** — {t.Description}"));
    }

    [KernelFunction, Description(
        "Returns the body of a named template — a procedure described in natural language with the " +
        "exact KernelFunction calls and parameters to perform. The caller (LLM) should execute the " +
        "described steps by invoking the referenced tools. Returns an error string if the name is unknown.")]
    public string GetTemplate(
        [Description("The template name as listed by ListTemplates (case-insensitive).")] string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Error: template name is required.";
        }
        var store = Load();
        var template = store.Templates.FirstOrDefault(
            t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        if (template is null)
        {
            return $"Error: no template named '{name}'. Call ListTemplates to see what's available.";
        }
        return template.Body;
    }

    private static TemplateStore Load()
    {
        try
        {
            if (File.Exists(DefaultStorePath))
            {
                var json = File.ReadAllText(DefaultStorePath);
                var loaded = JsonSerializer.Deserialize<TemplateStore>(json, JsonOptions);
                if (loaded is not null && loaded.Templates is not null)
                {
                    return loaded;
                }
            }
        }
        catch
        {
            // Fall through to defaults — never fail a chat turn over a
            // malformed user file.
        }

        var defaults = BuildDefaultStore();
        TrySave(defaults);
        return defaults;
    }

    private static void TrySave(TemplateStore store)
    {
        try
        {
            var dir = Path.GetDirectoryName(DefaultStorePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(DefaultStorePath, JsonSerializer.Serialize(store, JsonOptions));
        }
        catch
        {
            // Persistence is best-effort; templates still work in-memory.
        }
    }

    private static TemplateStore BuildDefaultStore() => new()
    {
        Templates = new List<TemplateEntry>
        {
            new()
            {
                Name = "flange-bolt-circle",
                Description = "Create a circular flange with a bolt-hole circle on the front face.",
                Body =
                    "Create a part if none is active (CreatePart). Then on the front plane create a sketch with " +
                    "a single circle centred on the origin with the requested outer diameter. Extrude it by the " +
                    "requested thickness (CreateExtrudedBoss). Then on the newly-created front face create a " +
                    "second sketch with N circles arranged on the requested pitch-circle diameter (use " +
                    "CreateCirclesOnBoltCircle if available, otherwise call CreateCircle N times). Cut-extrude " +
                    "those through-all (CreateExtrudedCut). Finish with ForceRebuild and report the resulting " +
                    "mass via GetMassProperties.",
            },
            new()
            {
                Name = "linear-bracket",
                Description = "L-shaped bracket with mounting holes on both legs.",
                Body =
                    "Create a part. Sketch an L profile (length × thickness, two legs) on the front plane and " +
                    "extrude (CreateExtrudedBoss) to the requested width. On each leg face, create a sketch " +
                    "with N evenly-spaced circles of the requested diameter and cut through-all " +
                    "(CreateExtrudedCut). Add edge fillets to the inside corner (CreateFillet). " +
                    "End with ForceRebuild and Screenshot the result.",
            },
            new()
            {
                Name = "drawing-from-active",
                Description = "Make a standard 3-view drawing of the active part with title block + BoM if assembly.",
                Body =
                    "Call CreateDrawingFromPart with the default template. Insert a model view " +
                    "(InsertModelViewFromActive) for front/top/right (InsertProjectionView for each). " +
                    "If the source is an assembly, also InsertBomTable. Update the title-block custom " +
                    "properties from the model (CopyCustomPropertiesToTitleBlock if available). " +
                    "Finally SaveAs alongside the source part.",
            },
            new()
            {
                Name = "mate-stack",
                Description = "Concentric + coincident pair for a typical bolt-in-hole assembly mate.",
                Body =
                    "In the active assembly, expect two cylindrical faces and two planar faces selected. " +
                    "Call AddMate(MateType=Concentric) on the cylindrical pair, then " +
                    "AddMate(MateType=Coincident) on the planar pair. Report any failures via " +
                    "EvaluateMateErrors.",
            },
            new()
            {
                Name = "inspect-drawing",
                Description = "Spelling + design-checker sweep with markdown summary.",
                Body =
                    "Call CheckSpelling on the active drawing, then RunDesignChecker. Combine both results into " +
                    "a single markdown report grouped by sheet/severity, and end with a one-line verdict.",
            },
        },
    };

    internal sealed class TemplateStore
    {
        public List<TemplateEntry> Templates { get; set; } = new();
    }

    internal sealed class TemplateEntry
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
    }
}

using Microsoft.SemanticKernel;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Copilot.Sw.Skills.Core;

/// <summary>
/// Material assignment and mass-property queries. These are read-mostly
/// skills the model can use to answer “how heavy is this?” / “what's the
/// COG?” without the user opening the dialog.
/// </summary>
public sealed class MaterialMassSkill : SldWorksSkillContext
{
    [KernelFunction(nameof(SetMaterial))]
    [Description("Apply a material to the active part. Pass the material " +
        "name (e.g. 'Plain Carbon Steel', '6061 Alloy', 'ABS'). " +
        "Uses the default SolidWorks material library unless 'database' " +
        "is provided.")]
    public void SetMaterial(string materialName, string? database = null, string? configurationName = null)
    {
        if (string.IsNullOrWhiteSpace(materialName))
        {
            throw new ArgumentException("Material name is required.", nameof(materialName));
        }

        var doc = ActiveSwDoc
            ?? throw new InvalidOperationException("No active SolidWorks document.");
        if (doc is not IPartDoc part)
        {
            throw new InvalidOperationException(
                "SetMaterial requires an active part document.");
        }

        var db = string.IsNullOrWhiteSpace(database)
            ? FindDefaultMaterialDb()
            : database!;

        part.SetMaterialPropertyName2(configurationName ?? string.Empty, db, materialName);
    }

    [KernelFunction(nameof(GetMassProperties))]
    [Description("Return the active part's mass properties as JSON: " +
        "mass (kg), volume (m^3), surface area (m^2), and center of mass " +
        "in metres relative to the part origin.")]
    public string GetMassProperties()
    {
        var doc = ActiveSwDoc
            ?? throw new InvalidOperationException("No active SolidWorks document.");

        var massProps = doc.Extension.CreateMassProperty();
        massProps.UseSystemUnits = true;

        var com = massProps.CenterOfMass as double[];

        var payload = new
        {
            mass_kg = massProps.Mass,
            volume_m3 = massProps.Volume,
            surfaceArea_m2 = massProps.SurfaceArea,
            centerOfMass_m = new
            {
                x = com is { Length: >= 3 } ? com[0] : 0.0,
                y = com is { Length: >= 3 } ? com[1] : 0.0,
                z = com is { Length: >= 3 } ? com[2] : 0.0,
            },
            documentUnit = SwUnits.GetDocumentLengthUnit(doc),
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
        });
    }

    private string FindDefaultMaterialDb()
    {
        // The standard SolidWorks materials database ships at
        // <SW install>\lang\<language>\sldmaterials\solidworks materials.sldmat.
        // The user may have rebranded this; if we can't find it, fall back
        // to the empty string which forces SolidWorks to use the default.
        try
        {
            var swPath = Sw?.GetExecutablePath();
            if (string.IsNullOrWhiteSpace(swPath))
            {
                return string.Empty;
            }
            var installDir = Path.GetDirectoryName(swPath);
            if (string.IsNullOrWhiteSpace(installDir))
            {
                return string.Empty;
            }
            var langDir = Path.Combine(installDir!, "lang", "english", "sldmaterials");
            var path = Path.Combine(langDir, "solidworks materials.sldmat");
            return File.Exists(path) ? path : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string Fmt(double d) => d.ToString("R", CultureInfo.InvariantCulture);
}

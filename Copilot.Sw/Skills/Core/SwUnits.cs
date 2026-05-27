using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Copilot.Sw.Skills.Core;

/// <summary>
/// Helpers for translating between the units the user (and the LLM) talks
/// in — millimetres, inches, degrees — and the metres / radians that the
/// SolidWorks API actually consumes.
///
/// SolidWorks always stores lengths in metres internally regardless of the
/// document's display unit, so every native skill that takes a numeric
/// length argument should funnel through <see cref="ToMeters"/>.
/// </summary>
internal static class SwUnits
{
    private const double InchToMeter = 0.0254;
    private const double FootToMeter = 0.3048;
    private const double DegToRad = System.Math.PI / 180.0;

    /// <summary>
    /// Convert a user-supplied length to metres. <paramref name="unit"/>
    /// may be <c>null</c>/empty (assume the active document's unit) or any
    /// of <c>mm</c>, <c>cm</c>, <c>m</c>, <c>in</c>, <c>"</c>, <c>ft</c>.
    /// </summary>
    public static double ToMeters(double value, string? unit, IModelDoc2? doc = null)
    {
        if (string.IsNullOrWhiteSpace(unit))
        {
            return value * GetDocumentLengthFactor(doc);
        }

        return unit!.Trim().ToLowerInvariant() switch
        {
            "mm" or "millimeter" or "millimetre" or "millimeters" or "millimetres" => value / 1000.0,
            "cm" or "centimeter" or "centimetre" or "centimeters" or "centimetres" => value / 100.0,
            "m" or "meter" or "metre" or "meters" or "metres" => value,
            "in" or "inch" or "inches" or "\"" => value * InchToMeter,
            "ft" or "foot" or "feet" or "'" => value * FootToMeter,
            _ => value * GetDocumentLengthFactor(doc),
        };
    }

    /// <summary>Convert metres back to the active document's display unit.</summary>
    public static double FromMeters(double meters, IModelDoc2? doc)
    {
        var factor = GetDocumentLengthFactor(doc);
        return factor == 0 ? meters : meters / factor;
    }

    /// <summary>Convert degrees to radians (SW takes angles in radians).</summary>
    public static double DegreesToRadians(double degrees) => degrees * DegToRad;

    /// <summary>Human-readable name of the active document's length unit.</summary>
    public static string GetDocumentLengthUnit(IModelDoc2? doc)
    {
        if (doc is null)
        {
            return "mm";
        }

        var lengthUnit = (swLengthUnit_e)doc.LengthUnit;
        return lengthUnit switch
        {
            swLengthUnit_e.swMM => "mm",
            swLengthUnit_e.swCM => "cm",
            swLengthUnit_e.swMETER => "m",
            swLengthUnit_e.swINCHES => "in",
            swLengthUnit_e.swFEET => "ft",
            swLengthUnit_e.swFEETINCHES => "ft",
            swLengthUnit_e.swMIL => "mil",
            swLengthUnit_e.swUIN => "uin",
            swLengthUnit_e.swANGSTROM => "A",
            swLengthUnit_e.swNANOMETER => "nm",
            swLengthUnit_e.swMICRON => "um",
            _ => "mm",
        };
    }

    private static double GetDocumentLengthFactor(IModelDoc2? doc)
    {
        // Returns the multiplier that turns a value expressed in the
        // document's display unit into metres. Defaults to mm.
        if (doc is null)
        {
            return 1.0 / 1000.0;
        }

        var lengthUnit = (swLengthUnit_e)doc.LengthUnit;
        return lengthUnit switch
        {
            swLengthUnit_e.swMM => 1.0 / 1000.0,
            swLengthUnit_e.swCM => 1.0 / 100.0,
            swLengthUnit_e.swMETER => 1.0,
            swLengthUnit_e.swINCHES => InchToMeter,
            swLengthUnit_e.swFEET => FootToMeter,
            swLengthUnit_e.swFEETINCHES => FootToMeter,
            swLengthUnit_e.swMIL => InchToMeter / 1000.0,
            swLengthUnit_e.swUIN => InchToMeter / 1_000_000.0,
            swLengthUnit_e.swNANOMETER => 1e-9,
            swLengthUnit_e.swMICRON => 1e-6,
            swLengthUnit_e.swANGSTROM => 1e-10,
            _ => 1.0 / 1000.0,
        };
    }
}

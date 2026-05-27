using Microsoft.SemanticKernel;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.ComponentModel;

namespace Copilot.Sw.Skills.Core;

/// <summary>
/// Mate creation in the active assembly. Pre-select the two entities the
/// mate is between (use <c>SelectByName</c> with append=true for the
/// second pick) before calling <c>AddMate</c>.
/// </summary>
public sealed class AssemblyMateSkill : SldWorksSkillContext
{
    [KernelFunction(nameof(AddMate))]
    [Description("Add a mate between the two currently selected entities. " +
        "Supported types: Coincident, Concentric, Parallel, Perpendicular, " +
        "Tangent, Distance, Angle, Lock. For Distance pass 'distance', for " +
        "Angle pass 'angleDegrees'. Returns the new mate's name.")]
    public string AddMate(
        [Description("Mate type name (Coincident / Concentric / Parallel / Perpendicular / Tangent / Distance / Angle / Lock).")] string type,
        [Description("Distance value, in the document's length unit (only used for Distance mates).")] double distance = 0,
        [Description("Angle value in degrees (only used for Angle mates).")] double angleDegrees = 0,
        [Description("Mate alignment: Aligned / AntiAligned / Closest.")] string alignment = "Closest",
        bool flip = false)
    {
        var asm = RequireAssembly();
        var doc = ActiveSwDoc!;

        var mateType = ParseMateType(type);
        var align = ParseAlign(alignment);
        var d = SwUnits.ToMeters(Math.Abs(distance), null, doc);
        var ang = SwUnits.DegreesToRadians(Math.Abs(angleDegrees));
        int errorStatus;

        var feat = asm.AddMate3(
            (int)mateType,
            (int)align,
            flip,
            d,                          // Distance
            d,                          // DistanceAbsUpperLimit
            d,                          // DistanceAbsLowerLimit
            0, 0,                       // GearRatio
            ang,                        // Angle
            ang, ang,                   // AngleAbsUpper/Lower
            false,                      // ForPositioningOnly
            out errorStatus) as IFeature;

        if (feat is null)
        {
            throw new InvalidOperationException(
                $"AddMate failed (error 0x{errorStatus:X}). Make sure exactly " +
                "two compatible entities are selected.");
        }
        return feat.Name;
    }

    private IAssemblyDoc RequireAssembly()
    {
        var doc = ActiveSwDoc
            ?? throw new InvalidOperationException("No active SolidWorks document.");
        if (doc is not IAssemblyDoc asm
            || (swDocumentTypes_e)doc.GetType() != swDocumentTypes_e.swDocASSEMBLY)
        {
            throw new InvalidOperationException(
                "Mate skills require an active assembly document.");
        }
        return asm;
    }

    private static swMateType_e ParseMateType(string type) =>
        type?.Trim().ToLowerInvariant() switch
        {
            "coincident" => swMateType_e.swMateCOINCIDENT,
            "concentric" => swMateType_e.swMateCONCENTRIC,
            "parallel" => swMateType_e.swMatePARALLEL,
            "perpendicular" => swMateType_e.swMatePERPENDICULAR,
            "tangent" => swMateType_e.swMateTANGENT,
            "distance" => swMateType_e.swMateDISTANCE,
            "angle" => swMateType_e.swMateANGLE,
            "lock" => swMateType_e.swMateLOCK,
            "symmetric" => swMateType_e.swMateSYMMETRIC,
            "width" => swMateType_e.swMateWIDTH,
            _ => throw new ArgumentException(
                $"Unknown mate type '{type}'.", nameof(type)),
        };

    private static swMateAlign_e ParseAlign(string alignment) =>
        alignment?.Trim().ToLowerInvariant() switch
        {
            "aligned" or "same" => swMateAlign_e.swMateAlignALIGNED,
            "antialigned" or "anti-aligned" or "against" => swMateAlign_e.swMateAlignANTI_ALIGNED,
            "closest" or "none" or null or "" => swMateAlign_e.swMateAlignCLOSEST,
            _ => swMateAlign_e.swMateAlignCLOSEST,
        };
}

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
    [Description("Add a mate between the currently selected entities. " +
        "Standard types: Coincident, Concentric, Parallel, Perpendicular, " +
        "Tangent, Distance, Angle, Lock, Symmetric, Width. " +
        "Advanced types: Cam (select cam profile faces + follower face), " +
        "Gear (select two cylindrical faces; pass gearRatioNumerator / " +
        "gearRatioDenominator), Slot (select cylindrical face + slot edge), " +
        "Path (select component vertex + path edge/curve), Hinge (select " +
        "two concentric + two coincident entities), Screw (two cylindrical " +
        "faces; pass 'distance' as pitch). Returns the new mate's name.")]
    public string AddMate(
        [Description("Mate type name (Coincident / Concentric / Parallel / Perpendicular / Tangent / Distance / Angle / Lock / Symmetric / Width / Cam / Gear / Slot / Path / Hinge / Screw / UniversalJoint / RackPinion / LinearCoupler / ProfileCenter).")] string type,
        [Description("Distance value, in the document's length unit. Distance/Screw mates: required. Screw: interpreted as pitch (length per revolution).")] double distance = 0,
        [Description("Angle value in degrees (only used for Angle mates).")] double angleDegrees = 0,
        [Description("Mate alignment: Aligned / AntiAligned / Closest.")] string alignment = "Closest",
        bool flip = false,
        [Description("Gear ratio numerator (Gear / RackPinion / Screw mates).")] double gearRatioNumerator = 1,
        [Description("Gear ratio denominator (Gear / RackPinion / Screw mates).")] double gearRatioDenominator = 1)
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
            gearRatioNumerator,
            gearRatioDenominator,
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
            "cam" or "camfollower" or "cam-follower" => swMateType_e.swMateCAMFOLLOWER,
            "gear" => swMateType_e.swMateGEAR,
            "slot" => swMateType_e.swMateSLOT,
            "path" => swMateType_e.swMatePATH,
            "hinge" => swMateType_e.swMateHINGE,
            "screw" => swMateType_e.swMateSCREW,
            "universaljoint" or "universal-joint" or "universal" => swMateType_e.swMateUNIVERSALJOINT,
            "rackpinion" or "rack-pinion" or "rack" => swMateType_e.swMateRACKPINION,
            "linearcoupler" or "linear-coupler" => swMateType_e.swMateLINEARCOUPLER,
            "profilecenter" or "profile-center" => swMateType_e.swMatePROFILECENTER,
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

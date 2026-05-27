using Copilot.Sw.Skills;
using Microsoft.SemanticKernel;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.ComponentModel;
using System.Linq;

namespace Copilot.Sw.Skills.Core;

/// <summary>
/// Remaining P1 sketch/feature operations: sketch mirror, simple tapped
/// hole via HoleWizard5, and body combine. Plus a typed
/// AddGlobalVariable() wrapper around EquationMgr.
/// </summary>
public sealed class P1ExtrasSkill : SldWorksSkillContext
{
    // -------- Sketch mirror --------

    [KernelFunction(nameof(MirrorSketchEntities))]
    [Description("Mirror the currently-selected sketch entities about the " +
        "currently-selected centre/construction line. Select the entities " +
        "first (any mark), then add the mirror line to the selection. " +
        "Must be inside an open sketch.")]
    public string MirrorSketchEntities()
    {
        var doc = ActiveSwDoc
            ?? throw new InvalidOperationException("No active SolidWorks document.");
        if (doc.SketchManager?.ActiveSketch is null)
        {
            throw new InvalidOperationException(
                "MirrorSketchEntities requires an open sketch. " +
                "Call InsertSketchOnPlane or EditSketch first.");
        }
        if (doc.SelectionManager is not ISelectionMgr sm
            || sm.GetSelectedObjectCount2(-1) < 2)
        {
            throw new InvalidOperationException(
                "Select at least one entity to mirror plus a centre/construction line.");
        }
        doc.SketchMirror();
        return "Sketch mirror applied.";
    }

    // -------- HoleWizard (tapped hole, the common case) --------

    [KernelFunction(nameof(InsertTappedHole))]
    [Description("Insert a HoleWizard tapped hole on the pre-selected face. " +
        "Select a planar face first; pass standard (ANSI_METRIC, ISO, DIN, " +
        "ANSI_INCH default), thread size string (e.g. 'M8x1.0', '1/4-20'), " +
        "depth (in document units), and end condition (BLIND or THROUGH). " +
        "Returns the new feature name.")]
    public string InsertTappedHole(
        string size,
        double depth = 0,
        string standard = "ISO",
        string endCondition = "BLIND",
        string? unit = null)
    {
        if (string.IsNullOrWhiteSpace(size))
        {
            throw new ArgumentException("Thread size is required (e.g. 'M8x1.0').", nameof(size));
        }
        var doc = ActiveSwDoc
            ?? throw new InvalidOperationException("No active SolidWorks document.");
        if ((swDocumentTypes_e)doc.GetType() != swDocumentTypes_e.swDocPART)
        {
            throw new InvalidOperationException("InsertTappedHole requires an active part.");
        }
        if (doc.SelectionManager is not ISelectionMgr sm || sm.GetSelectedObjectCount2(-1) < 1)
        {
            throw new InvalidOperationException(
                "Select a planar face before calling InsertTappedHole.");
        }

        var (stdIdx, fastenerIdx) = ResolveTappedHoleStandard(standard);
        var end = endCondition?.Trim().ToUpperInvariant() switch
        {
            "THROUGH" or "THROUGHALL" or "THROUGH_ALL" => swEndConditions_e.swEndCondThroughAll,
            "MIDPLANE" => swEndConditions_e.swEndCondMidPlane,
            _ => swEndConditions_e.swEndCondBlind,
        };

        var depthM = end == swEndConditions_e.swEndCondThroughAll
            ? 0.01 // ignored by SW for through-all but cannot be 0
            : SwUnits.ToMeters(depth > 0 ? depth : 10, unit, doc);

        var fm = doc.FeatureManager;
        var feat = fm.HoleWizard5(
            GenericHoleType: (int)swWzdGeneralHoleTypes_e.swWzdTap,
            StandardIndex: stdIdx,
            FastenerTypeIndex: fastenerIdx,
            SSize: size,
            EndType: (short)end,
            Diameter: 0, // driven by table
            Depth: depthM,
            Length: depthM,
            Value1: 0, Value2: 0, Value3: 0, Value4: 0, Value5: 0, Value6: 0,
            Value7: 0, Value8: 0, Value9: 0, Value10: 0, Value11: 0, Value12: 0,
            ThreadClass: "",
            RevDir: false,
            FeatureScope: true,
            AutoSelect: true,
            AssemblyFeatureScope: false,
            AutoSelectComponents: false,
            PropagateFeatureToParts: false) as IFeature
            ?? throw new InvalidOperationException(
                $"HoleWizard failed for size '{size}'. Verify the thread " +
                "size string matches an entry in the chosen standard's table.");
        return feat.Name;
    }

    private static (int standardIndex, int fastenerIndex) ResolveTappedHoleStandard(string standard)
    {
        var s = standard?.Trim().ToUpperInvariant() ?? "ISO";
        return s switch
        {
            "ANSI_METRIC" or "ANSIMETRIC" => (
                (int)swWzdHoleStandards_e.swStandardAnsiMetric,
                (int)swWzdHoleStandardFastenerTypes_e.swStandardAnsiMetricTappedHole),
            "DIN" => (
                (int)swWzdHoleStandards_e.swStandardDIN,
                (int)swWzdHoleStandardFastenerTypes_e.swStandardDINTappedHole),
            "JIS" => (
                (int)swWzdHoleStandards_e.swStandardJIS,
                (int)swWzdHoleStandardFastenerTypes_e.swStandardJISTappedHole),
            "BSI" => (
                (int)swWzdHoleStandards_e.swStandardBSI,
                (int)swWzdHoleStandardFastenerTypes_e.swStandardBSITappedHole),
            "GB" => (
                (int)swWzdHoleStandards_e.swStandardGB,
                (int)swWzdHoleStandardFastenerTypes_e.swStandardGBTappedHole),
            "ANSI_INCH" or "ANSIINCH" or "ANSI" => (
                (int)swWzdHoleStandards_e.swStandardAnsiInch,
                (int)swWzdHoleStandardFastenerTypes_e.swStandardAnsiInchTappedHole),
            _ => (
                (int)swWzdHoleStandards_e.swStandardISO,
                (int)swWzdHoleStandardFastenerTypes_e.swStandardISOTappedHole),
        };
    }

    // -------- HoleWizard variants: counter-bore / counter-sink / pipe-tap --------

    [KernelFunction(nameof(InsertCounterBoreHole))]
    [Description("Insert a HoleWizard counter-bored hole sized for a socket-" +
        "head cap screw on the pre-selected planar face. 'size' uses the " +
        "fastener-table format ('M8', '1/4', '#10'). 'standard' is ISO " +
        "(default), ANSI_METRIC, DIN, ANSI_INCH, BSI, JIS or GB. " +
        "'endCondition' is BLIND / THROUGH / MIDPLANE.")]
    public string InsertCounterBoreHole(
        string size,
        double depth = 0,
        string standard = "ISO",
        string endCondition = "BLIND",
        string? unit = null)
        => InsertHoleWizardVariant(
            swWzdGeneralHoleTypes_e.swWzdCounterBore,
            ResolveCounterBoreStandard(standard),
            size, depth, endCondition, unit);

    [KernelFunction(nameof(InsertCounterSinkHole))]
    [Description("Insert a HoleWizard counter-sunk hole sized for a flat-" +
        "head screw on the pre-selected planar face. Arguments mirror " +
        "InsertCounterBoreHole.")]
    public string InsertCounterSinkHole(
        string size,
        double depth = 0,
        string standard = "ISO",
        string endCondition = "BLIND",
        string? unit = null)
        => InsertHoleWizardVariant(
            swWzdGeneralHoleTypes_e.swWzdCounterSink,
            ResolveCounterSinkStandard(standard),
            size, depth, endCondition, unit);

    [KernelFunction(nameof(InsertPipeTapHole))]
    [Description("Insert a HoleWizard tapered pipe-tap hole on the pre-" +
        "selected planar face. 'size' is a pipe-thread designation like " +
        "'1/8' or '1/4'. 'standard' is ISO (default), ANSI_INCH, DIN, " +
        "BSI, JIS or GB.")]
    public string InsertPipeTapHole(
        string size,
        double depth = 0,
        string standard = "ISO",
        string endCondition = "BLIND",
        string? unit = null)
        => InsertHoleWizardVariant(
            swWzdGeneralHoleTypes_e.swWzdPipeTap,
            ResolvePipeTapStandard(standard),
            size, depth, endCondition, unit);

    private string InsertHoleWizardVariant(
        swWzdGeneralHoleTypes_e holeType,
        (int standardIndex, int fastenerIndex) idx,
        string size,
        double depth,
        string endCondition,
        string? unit)
    {
        if (string.IsNullOrWhiteSpace(size))
        {
            throw new ArgumentException("Hole size is required.", nameof(size));
        }
        var doc = ActiveSwDoc
            ?? throw new InvalidOperationException("No active SolidWorks document.");
        if ((swDocumentTypes_e)doc.GetType() != swDocumentTypes_e.swDocPART)
        {
            throw new InvalidOperationException("HoleWizard variants require an active part.");
        }
        if (doc.SelectionManager is not ISelectionMgr sm || sm.GetSelectedObjectCount2(-1) < 1)
        {
            throw new InvalidOperationException("Select a planar face first.");
        }

        var end = endCondition?.Trim().ToUpperInvariant() switch
        {
            "THROUGH" or "THROUGHALL" or "THROUGH_ALL" => swEndConditions_e.swEndCondThroughAll,
            "MIDPLANE" => swEndConditions_e.swEndCondMidPlane,
            _ => swEndConditions_e.swEndCondBlind,
        };
        var depthM = end == swEndConditions_e.swEndCondThroughAll
            ? 0.01
            : SwUnits.ToMeters(depth > 0 ? depth : 10, unit, doc);

        var feat = doc.FeatureManager.HoleWizard5(
            GenericHoleType: (int)holeType,
            StandardIndex: idx.standardIndex,
            FastenerTypeIndex: idx.fastenerIndex,
            SSize: size,
            EndType: (short)end,
            Diameter: 0,
            Depth: depthM,
            Length: depthM,
            Value1: 0, Value2: 0, Value3: 0, Value4: 0, Value5: 0, Value6: 0,
            Value7: 0, Value8: 0, Value9: 0, Value10: 0, Value11: 0, Value12: 0,
            ThreadClass: "",
            RevDir: false,
            FeatureScope: true,
            AutoSelect: true,
            AssemblyFeatureScope: false,
            AutoSelectComponents: false,
            PropagateFeatureToParts: false) as IFeature
            ?? throw new InvalidOperationException(
                $"HoleWizard failed for {holeType} size '{size}' under standard index {idx.standardIndex}. " +
                "Verify the size string matches a row in the chosen fastener table.");
        return feat.Name;
    }

    private static (int standardIndex, int fastenerIndex) ResolveCounterBoreStandard(string standard)
    {
        // Socket-head-cap-screw is the canonical counter-bore fastener
        // across every standard SolidWorks ships.
        var s = standard?.Trim().ToUpperInvariant() ?? "ISO";
        return s switch
        {
            "ANSI_METRIC" or "ANSIMETRIC" => (
                (int)swWzdHoleStandards_e.swStandardAnsiMetric,
                (int)swWzdHoleStandardFastenerTypes_e.swStandardAnsiMetricSocketHeadCapScrew),
            "ANSI_INCH" or "ANSIINCH" or "ANSI" => (
                (int)swWzdHoleStandards_e.swStandardAnsiInch,
                (int)swWzdHoleStandardFastenerTypes_e.swStandardAnsiInchSocketCapScrew),
            _ => (
                (int)swWzdHoleStandards_e.swStandardISO,
                (int)swWzdHoleStandardFastenerTypes_e.swStandardISOSocketHeadCap),
        };
    }

    private static (int standardIndex, int fastenerIndex) ResolveCounterSinkStandard(string standard)
    {
        // 82° flat head is the most common counter-sink fastener.
        var s = standard?.Trim().ToUpperInvariant() ?? "ISO";
        return s switch
        {
            "ANSI_METRIC" or "ANSIMETRIC" => (
                (int)swWzdHoleStandards_e.swStandardAnsiMetric,
                (int)swWzdHoleStandardFastenerTypes_e.swStandardAnsiMetricFlatHead82),
            "ANSI_INCH" or "ANSIINCH" or "ANSI" => (
                (int)swWzdHoleStandards_e.swStandardAnsiInch,
                (int)swWzdHoleStandardFastenerTypes_e.swStandardAnsiInchFlatHead82),
            _ => (
                (int)swWzdHoleStandards_e.swStandardISO,
                (int)swWzdHoleStandardFastenerTypes_e.swStandardISOCTSKFlatHead),
        };
    }

    private static (int standardIndex, int fastenerIndex) ResolvePipeTapStandard(string standard)
    {
        var s = standard?.Trim().ToUpperInvariant() ?? "ISO";
        return s switch
        {
            "ANSI_INCH" or "ANSIINCH" or "ANSI" => (
                (int)swWzdHoleStandards_e.swStandardAnsiInch,
                (int)swWzdHoleStandardFastenerTypes_e.swStandardAnsiInchTaperedPipeTap),
            "DIN" => (
                (int)swWzdHoleStandards_e.swStandardDIN,
                (int)swWzdHoleStandardFastenerTypes_e.swStandardDINTaperedPipeTap),
            "JIS" => (
                (int)swWzdHoleStandards_e.swStandardJIS,
                (int)swWzdHoleStandardFastenerTypes_e.swStandardJISTaperedPipeTap),
            "BSI" => (
                (int)swWzdHoleStandards_e.swStandardBSI,
                (int)swWzdHoleStandardFastenerTypes_e.swStandardBSITaperedPipeTap),
            "GB" => (
                (int)swWzdHoleStandards_e.swStandardGB,
                (int)swWzdHoleStandardFastenerTypes_e.swStandardGBTaperedPipeTap),
            _ => (
                (int)swWzdHoleStandards_e.swStandardISO,
                (int)swWzdHoleStandardFastenerTypes_e.swStandardISOTaperedPipeTap),
        };
    }

    // -------- Combine bodies --------

    [KernelFunction(nameof(CombineBodies))]
    [Description("Combine all solid bodies in the active part using the " +
        "given operation: ADD, SUBTRACT, or COMMON. The first body in the " +
        "part becomes the 'main' body; the rest are 'tools'. Requires at " +
        "least 2 solid bodies. Returns the new feature name.")]
    public string CombineBodies(string operation = "ADD")
    {
        var doc = ActiveSwDoc
            ?? throw new InvalidOperationException("No active SolidWorks document.");
        if (doc is not IPartDoc part)
        {
            throw new InvalidOperationException("CombineBodies requires an active part.");
        }

        var op = operation?.Trim().ToUpperInvariant() switch
        {
            "SUBTRACT" or "CUT" => swBodyOperationType_e.SWBODYCUT,
            "COMMON" or "INTERSECT" => swBodyOperationType_e.SWBODYINTERSECT,
            _ => swBodyOperationType_e.SWBODYADD,
        };

        var bodies = (part.GetBodies2((int)swBodyType_e.swSolidBody, true) as object[])
            ?.OfType<Body2>()
            .ToArray() ?? Array.Empty<Body2>();
        if (bodies.Length < 2)
        {
            throw new InvalidOperationException(
                $"CombineBodies needs at least 2 solid bodies (found {bodies.Length}).");
        }

        var main = bodies[0];
        var tools = bodies.Skip(1).ToArray();
        var feat = doc.FeatureManager.InsertCombineFeature((int)op, main, tools) as IFeature
            ?? throw new InvalidOperationException(
                "InsertCombineFeature failed. Verify bodies overlap for " +
                $"{op} (e.g. SUBTRACT/COMMON require intersection).");
        return feat.Name;
    }

    // -------- Global variables --------

    [KernelFunction(nameof(AddGlobalVariable))]
    [Description("Add or update a SolidWorks global variable. Example: " +
        "name='Width', expression='50'. The expression may reference other " +
        "variables and use units (e.g. '\"Length\"*2 + 5mm').")]
    public string AddGlobalVariable(string name, string expression)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Variable name is required.", nameof(name));
        }
        if (string.IsNullOrWhiteSpace(expression))
        {
            throw new ArgumentException("Expression is required.", nameof(expression));
        }
        var doc = ActiveSwDoc
            ?? throw new InvalidOperationException("No active SolidWorks document.");
        var mgr = doc.GetEquationMgr()
            ?? throw new InvalidOperationException("EquationManager unavailable.");

        var equation = $"\"{name}\" = {expression}";
        var idx = mgr.Add3(-1, equation, true,
            (int)swInConfigurationOpts_e.swAllConfiguration, null);
        if (idx < 0)
        {
            throw new InvalidOperationException(
                $"Failed to add global variable '{name}'. " +
                "Verify the expression syntax.");
        }
        return $"Global variable '{name}' = {expression} (index {idx}).";
    }
}

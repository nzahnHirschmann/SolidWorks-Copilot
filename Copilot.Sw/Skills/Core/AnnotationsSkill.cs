using Copilot.Sw.Skills;
using Microsoft.SemanticKernel;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.ComponentModel;

namespace Copilot.Sw.Skills.Core;

/// <summary>
/// Drawing / part annotations: notes, datum tags, geometric tolerances,
/// surface finish, centre marks. The QA half of "tell Copilot to check a
/// drawing" leans on these — every accuracy rule reads what's on the
/// sheet, and these skills are how the model can also *add* the missing
/// callouts.
/// </summary>
public sealed class AnnotationsSkill : SldWorksSkillContext
{
    // -------- Notes --------

    [KernelFunction(nameof(AddNote))]
    [Description("Add a free-text note. In a drawing, (x, y) is the note " +
        "anchor in mm on the active sheet; in a part, it's a 3-D point in " +
        "document units. Returns the note's annotation name.")]
    public string AddNote(string text, double x = 0, double y = 0, double z = 0, string? unit = null)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }
        var doc = ActiveSwDoc
            ?? throw new InvalidOperationException("No active SolidWorks document.");

        if (doc.InsertNote(text) is not INote note)
        {
            throw new InvalidOperationException("InsertNote returned null.");
        }
        var mx = SwUnits.ToMeters(x, unit, doc);
        var my = SwUnits.ToMeters(y, unit, doc);
        var mz = SwUnits.ToMeters(z, unit, doc);
        note.SetTextPoint(mx, my, mz);

        doc.ClearSelection2(true);
        var ann = note.GetAnnotation() as IAnnotation;
        return ann?.GetName() ?? "<note>";
    }

    // -------- Datum tag --------

    [KernelFunction(nameof(AddDatumFeature))]
    [Description("Add a datum feature symbol on the pre-selected edge or " +
        "face. Pass the datum letter (e.g. 'A'). Returns the annotation name.")]
    public string AddDatumFeature(string label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            throw new ArgumentException("Datum label is required (e.g. 'A').", nameof(label));
        }
        var doc = ActiveSwDoc
            ?? throw new InvalidOperationException("No active SolidWorks document.");
        if (doc.SelectionManager is not ISelectionMgr sm || sm.GetSelectedObjectCount2(-1) < 1)
        {
            throw new InvalidOperationException(
                "Select a planar face or edge before calling AddDatumFeature.");
        }

        if (doc.InsertDatumTag2() is not IDatumTag tag)
        {
            throw new InvalidOperationException(
                "InsertDatumTag2 returned null. Verify the selection is a face/edge.");
        }
        tag.SetLabel(label);
        var ann = tag.GetAnnotation() as IAnnotation;
        return ann?.GetName() ?? $"DatumTag-{label}";
    }

    // -------- Geometric tolerance --------

    [KernelFunction(nameof(AddGeometricTolerance))]
    [Description("Add a geometric tolerance frame on the pre-selected " +
        "edge/face. 'symbol' is one of POSITION, FLATNESS, STRAIGHTNESS, " +
        "PERPENDICULARITY, PARALLELISM, ANGULARITY, CIRCULARITY, " +
        "CYLINDRICITY, PROFILE_LINE, PROFILE_SURFACE, RUNOUT, TOTAL_RUNOUT, " +
        "SYMMETRY, CONCENTRICITY. 'tolerance' is the tolerance string " +
        "(e.g. '0.1', 'Ø0.05 M'). Datums A/B/C are optional.")]
    public string AddGeometricTolerance(
        string symbol,
        string tolerance,
        string datumA = "",
        string datumB = "",
        string datumC = "")
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            throw new ArgumentException("symbol is required.", nameof(symbol));
        }
        if (string.IsNullOrWhiteSpace(tolerance))
        {
            throw new ArgumentException("tolerance is required.", nameof(tolerance));
        }
        var doc = ActiveSwDoc
            ?? throw new InvalidOperationException("No active SolidWorks document.");
        if (doc.SelectionManager is not ISelectionMgr sm || sm.GetSelectedObjectCount2(-1) < 1)
        {
            throw new InvalidOperationException(
                "Select an edge or face before calling AddGeometricTolerance.");
        }

        var gcs = MapGtolSymbol(symbol);
        if (doc.InsertGtol() is not IGtol gtol)
        {
            throw new InvalidOperationException("InsertGtol returned null.");
        }
        gtol.SetFrameValues2(0, tolerance, "", datumA, datumB, datumC);
        gtol.SetFrameSymbols2(0, gcs, false, "", false, "", "", "", "");

        var ann = gtol.GetAnnotation() as IAnnotation;
        return ann?.GetName() ?? $"Gtol-{symbol}";
    }

    private static string MapGtolSymbol(string symbol) => symbol.Trim().ToUpperInvariant() switch
    {
        "POSITION" or "POS" => "n7",
        "FLATNESS" => "e1",
        "STRAIGHTNESS" => "e2",
        "PERPENDICULARITY" or "PERP" => "p1",
        "PARALLELISM" or "PAR" => "p2",
        "ANGULARITY" => "p3",
        "CIRCULARITY" or "ROUNDNESS" => "f1",
        "CYLINDRICITY" => "f2",
        "PROFILE_LINE" or "PROFILELINE" => "f3",
        "PROFILE_SURFACE" or "PROFILESURFACE" => "f4",
        "RUNOUT" or "CIRCULAR_RUNOUT" => "c1",
        "TOTAL_RUNOUT" => "c2",
        "SYMMETRY" => "s1",
        "CONCENTRICITY" => "s2",
        _ => throw new ArgumentException(
            $"Unknown geometric tolerance symbol '{symbol}'. " +
            "Use POSITION, FLATNESS, STRAIGHTNESS, PERPENDICULARITY, PARALLELISM, " +
            "ANGULARITY, CIRCULARITY, CYLINDRICITY, PROFILE_LINE, PROFILE_SURFACE, " +
            "RUNOUT, TOTAL_RUNOUT, SYMMETRY, CONCENTRICITY.",
            nameof(symbol)),
    };

    // -------- Surface finish --------

    [KernelFunction(nameof(AddSurfaceFinish))]
    [Description("Add a surface-finish symbol on the pre-selected face/edge. " +
        "Pass 'maxRoughness' as the Ra value (e.g. '1.6', '3.2'). " +
        "'symbolType' is BASIC, MACHINING (default), or DONT_MACHINE.")]
    public string AddSurfaceFinish(string maxRoughness, string symbolType = "MACHINING")
    {
        if (string.IsNullOrWhiteSpace(maxRoughness))
        {
            throw new ArgumentException("maxRoughness is required.", nameof(maxRoughness));
        }
        var doc = ActiveSwDoc
            ?? throw new InvalidOperationException("No active SolidWorks document.");
        if (doc.SelectionManager is not ISelectionMgr sm || sm.GetSelectedObjectCount2(-1) < 1)
        {
            throw new InvalidOperationException(
                "Select a face or edge before calling AddSurfaceFinish.");
        }

        var sym = symbolType?.Trim().ToUpperInvariant() switch
        {
            "BASIC" => swSFSymType_e.swSFBasic,
            "DONT_MACHINE" or "NO_MACHINING" => swSFSymType_e.swSFDont_Machine,
            _ => swSFSymType_e.swSFMachining_Req,
        };

        var ok = doc.InsertSurfaceFinishSymbol2(
            SymType: (int)sym,
            LeaderType: 0,
            LocX: 0, LocY: 0, LocZ: 0,
            LaySymbol: 0,
            ArrowType: 0,
            MachAllowance: "",
            OtherVals: "",
            ProdMethod: "",
            SampleLen: "",
            MaxRoughness: maxRoughness,
            MinRoughness: "",
            RoughnessSpacing: "");
        if (!ok)
        {
            throw new InvalidOperationException(
                "InsertSurfaceFinishSymbol2 failed. Verify the selection.");
        }
        return $"Surface finish Ra {maxRoughness} ({sym}) added.";
    }

    // -------- Centre marks (drawings only) --------

    [KernelFunction(nameof(InsertCenterMarks))]
    [Description("Insert centre marks on the pre-selected circular edge(s) " +
        "in the active drawing. 'style' is SINGLE (default), LINEAR, or " +
        "CIRCULAR.")]
    public string InsertCenterMarks(string style = "SINGLE", bool propagate = true)
    {
        var doc = ActiveSwDoc
            ?? throw new InvalidOperationException("No active SolidWorks document.");
        if (doc is not IDrawingDoc dwg
            || (swDocumentTypes_e)doc.GetType() != swDocumentTypes_e.swDocDRAWING)
        {
            throw new InvalidOperationException("InsertCenterMarks requires an active drawing.");
        }
        if (doc.SelectionManager is not ISelectionMgr sm || sm.GetSelectedObjectCount2(-1) < 1)
        {
            throw new InvalidOperationException(
                "Select one or more circular edges before calling InsertCenterMarks.");
        }

        var styleEnum = style?.Trim().ToUpperInvariant() switch
        {
            "LINEAR" => swCenterMarkStyle_e.swCenterMark_LinearGroup,
            "CIRCULAR" => swCenterMarkStyle_e.swCenterMark_CircularGroup,
            _ => swCenterMarkStyle_e.swCenterMark_Single,
        };
        var cm = dwg.InsertCenterMark3((int)styleEnum, propagate, false)
            ?? throw new InvalidOperationException(
                "InsertCenterMark3 returned null. Verify the selection.");
        return $"Centre mark ({styleEnum}) inserted.";
    }
}

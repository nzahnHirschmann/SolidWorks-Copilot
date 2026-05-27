using Microsoft.SemanticKernel;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.ComponentModel;
using System.IO;

namespace Copilot.Sw.Skills.Core;

/// <summary>
/// Drawing authoring: create a drawing from an open part/assembly,
/// drop views onto sheets, and bring in model-driving dimensions.
/// </summary>
public sealed class DrawingCreationSkill : SldWorksSkillContext
{
    [KernelFunction(nameof(CreateDrawingFromPart))]
    [Description("Create a new drawing document with the active part or " +
        "assembly placed as three standard views (front/top/right, 3rd " +
        "angle). Returns the new drawing's title.")]
    public string CreateDrawingFromPart(string? sheetSize = null, bool insertModelDimensions = true)
    {
        var sw = Sw
            ?? throw new InvalidOperationException("SolidWorks is not running.");
        var source = sw.IActiveDoc2
            ?? throw new InvalidOperationException(
                "No active document. Open the part/assembly first.");

        var srcType = (swDocumentTypes_e)source.GetType();
        if (srcType != swDocumentTypes_e.swDocPART
            && srcType != swDocumentTypes_e.swDocASSEMBLY)
        {
            throw new InvalidOperationException(
                "CreateDrawingFromPart needs an active part or assembly.");
        }
        if (string.IsNullOrWhiteSpace(source.GetPathName()))
        {
            throw new InvalidOperationException(
                "Save the part/assembly to disk before generating a drawing " +
                "(SolidWorks needs the on-disk path to reference views).");
        }

        var template = sw.GetUserPreferenceStringValue(
            (int)swUserPreferenceStringValue_e.swDefaultTemplateDrawing) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(template) || !File.Exists(template))
        {
            throw new InvalidOperationException(
                "No default drawing template configured. Set one in " +
                "Tools > Options > Default Templates.");
        }

        var paper = ParsePaperSize(sheetSize);
        var drawing = sw.NewDocument(template, (int)paper, 0, 0) as IDrawingDoc
            ?? throw new InvalidOperationException(
                "SolidWorks refused to create a new drawing from the template.");

        var ok = drawing.Create3rdAngleViews2(source.GetPathName());
        if (!ok)
        {
            throw new InvalidOperationException(
                "Create3rdAngleViews2 failed. Check that the source model " +
                "is fully resolved and saved.");
        }

        if (insertModelDimensions)
        {
            drawing.InsertModelAnnotations3(
                (int)swImportModelItemsSource_e.swImportModelItemsFromEntireModel,
                (int)swInsertAnnotation_e.swInsertDimensionsMarkedForDrawing,
                AllViews: true,
                DuplicateDims: false,
                HiddenFeatureDims: false,
                UsePlacementInSketch: true);
        }

        return ((IModelDoc2)drawing).GetTitle();
    }

    [KernelFunction(nameof(InsertNamedView))]
    [Description("Insert a single named view of the given model into the " +
        "active drawing at (x, y). viewName is a SolidWorks orientation " +
        "name like '*Front', '*Top', '*Right', '*Isometric', '*Trimetric', " +
        "or any saved named view.")]
    public string InsertNamedView(
        string modelPath, string viewName,
        double x, double y, string? unit = null)
    {
        var dwg = RequireDrawing();
        var mx = SwUnits.ToMeters(x, unit, (IModelDoc2)dwg);
        var my = SwUnits.ToMeters(y, unit, (IModelDoc2)dwg);
        var view = dwg.CreateDrawViewFromModelView3(modelPath, viewName, mx, my, 0) as IView
            ?? throw new InvalidOperationException(
                $"Could not insert view '{viewName}' of '{modelPath}'.");
        return view.GetName2();
    }

    [KernelFunction(nameof(InsertSectionView))]
    [Description("Insert a section view at (x, y) on the active drawing. " +
        "Pre-create a section line via a sketch line on the parent view, " +
        "or this call will operate on the currently selected entities.")]
    public string InsertSectionView(double x, double y, string label = "A", string? unit = null)
    {
        var dwg = RequireDrawing();
        var mx = SwUnits.ToMeters(x, unit, (IModelDoc2)dwg);
        var my = SwUnits.ToMeters(y, unit, (IModelDoc2)dwg);
        var view = dwg.CreateSectionViewAt5(mx, my, 0, label, 0, null, 0) as IView
            ?? throw new InvalidOperationException(
                "CreateSectionView failed. Pre-select the section line.");
        return view.GetName2();
    }

    [KernelFunction(nameof(InsertDetailView))]
    [Description("Insert a detail view at (x, y) showing a magnified " +
        "region of the parent view. Pre-select the circular sketch entity " +
        "that bounds the detail region.")]
    public string InsertDetailView(
        double x, double y,
        double scale = 2.0,
        string label = "A",
        string? unit = null)
    {
        var dwg = RequireDrawing();
        var mx = SwUnits.ToMeters(x, unit, (IModelDoc2)dwg);
        var my = SwUnits.ToMeters(y, unit, (IModelDoc2)dwg);
        var view = dwg.CreateDetailViewAt4(
            mx, my, 0,
            Style: (int)swDetViewStyle_e.swDetViewSTANDARD,
            Scale1: scale, Scale2: 1.0,
            LabelIn: label,
            Showtype: (int)swDetCircleShowType_e.swDetCircleCIRCLE,
            FullOutline: false,
            JaggedOutline: false,
            NoOutline: false,
            ShapeIntensity: 0) as IView
            ?? throw new InvalidOperationException(
                "CreateDetailView failed. Pre-select the detail circle.");
        return view.GetName2();
    }

    [KernelFunction(nameof(InsertAuxiliaryView))]
    [Description("Insert an auxiliary view at (x, y), aligned to the " +
        "currently selected edge or line on the parent view.")]
    public string InsertAuxiliaryView(double x, double y, string label = "A", string? unit = null)
    {
        var dwg = RequireDrawing();
        var mx = SwUnits.ToMeters(x, unit, (IModelDoc2)dwg);
        var my = SwUnits.ToMeters(y, unit, (IModelDoc2)dwg);
        var view = dwg.CreateAuxiliaryViewAt2(mx, my, 0, false, label, true, false) as IView
            ?? throw new InvalidOperationException(
                "CreateAuxiliaryView failed. Pre-select an edge on the parent view.");
        return view.GetName2();
    }

    [KernelFunction(nameof(InsertModelDimensions))]
    [Description("Pull all model-driving dimensions (marked for drawing) " +
        "from the referenced model into every view of the active drawing.")]
    public void InsertModelDimensions()
    {
        var dwg = RequireDrawing();
        dwg.InsertModelAnnotations3(
            (int)swImportModelItemsSource_e.swImportModelItemsFromEntireModel,
            (int)swInsertAnnotation_e.swInsertDimensionsMarkedForDrawing,
            AllViews: true,
            DuplicateDims: false,
            HiddenFeatureDims: false,
            UsePlacementInSketch: true);
    }

    [KernelFunction(nameof(AddSheet))]
    [Description("Add a new sheet to the active drawing with the given " +
        "name and paper size (A0..A4, A..E, Letter). Returns the new " +
        "sheet's name.")]
    public string AddSheet(string name, string? sheetSize = null, double scaleNumerator = 1, double scaleDenominator = 1)
    {
        var dwg = RequireDrawing();
        var paper = ParsePaperSize(sheetSize);
        if (!dwg.NewSheet3(
                name,
                (int)paper,
                (int)swDwgTemplates_e.swDwgTemplateNone,
                Math.Max(0.001, scaleNumerator),
                Math.Max(0.001, scaleDenominator),
                FirstAngle: false,
                TemplateName: string.Empty,
                Width: 0, Height: 0,
                PropertyViewName: string.Empty))
        {
            throw new InvalidOperationException($"NewSheet failed for '{name}'.");
        }
        return name;
    }

    [KernelFunction(nameof(ActivateSheet))]
    [Description("Activate the named sheet in the active drawing.")]
    public void ActivateSheet(string name)
    {
        var dwg = RequireDrawing();
        if (!dwg.ActivateSheet(name))
        {
            throw new InvalidOperationException($"Sheet '{name}' not found.");
        }
    }

    [KernelFunction(nameof(InsertProjectedView))]
    [Description("Insert a projected (unfolded) view from a pre-selected " +
        "parent view. (x, y) is the new view position in mm relative to " +
        "the sheet, and indirectly determines the projection direction.")]
    public string InsertProjectedView(double x, double y, bool notAligned = false)
    {
        var dwg = RequireDrawing();
        var doc = (IModelDoc2)dwg;
        if (doc.SelectionManager is not ISelectionMgr sm || sm.GetSelectedObjectCount2(-1) < 1)
        {
            throw new InvalidOperationException(
                "Select a parent drawing view before calling InsertProjectedView.");
        }
        var view = dwg.CreateUnfoldedViewAt3(x / 1000.0, y / 1000.0, 0, notAligned)
            ?? throw new InvalidOperationException("CreateUnfoldedViewAt3 returned null.");
        return view.GetName2();
    }

    [KernelFunction(nameof(InsertCenterlines))]
    [Description("Insert a centreline between the two pre-selected parallel " +
        "edges in the active drawing.")]
    public string InsertCenterlines()
    {
        var dwg = RequireDrawing();
        var doc = (IModelDoc2)dwg;
        if (doc.SelectionManager is not ISelectionMgr sm || sm.GetSelectedObjectCount2(-1) < 2)
        {
            throw new InvalidOperationException(
                "Select two parallel edges before calling InsertCenterlines.");
        }
        var cl = dwg.InsertCenterLine2()
            ?? throw new InvalidOperationException("InsertCenterLine2 returned null.");
        return "Centreline inserted.";
    }

    [KernelFunction(nameof(AutoBalloonViews))]
    [Description("Insert auto-balloons on the pre-selected drawing view(s) " +
        "(assembly views only). 'layout' is SQUARE (default), CIRCULAR, " +
        "TOP, BOTTOM, LEFT, or RIGHT. Returns the balloon count.")]
    public string AutoBalloonViews(string layout = "SQUARE")
    {
        var dwg = RequireDrawing();
        var doc = (IModelDoc2)dwg;
        if (doc.SelectionManager is not ISelectionMgr sm || sm.GetSelectedObjectCount2(-1) < 1)
        {
            throw new InvalidOperationException(
                "Select an assembly drawing view before calling AutoBalloonViews.");
        }
        var layoutId = layout?.Trim().ToUpperInvariant() switch
        {
            "CIRCULAR" or "CIRCLE" => (int)swBalloonLayoutType_e.swDetailingBalloonLayout_Circle,
            "TOP" => (int)swBalloonLayoutType_e.swDetailingBalloonLayout_Top,
            "BOTTOM" => (int)swBalloonLayoutType_e.swDetailingBalloonLayout_Bottom,
            "LEFT" => (int)swBalloonLayoutType_e.swDetailingBalloonLayout_Left,
            "RIGHT" => (int)swBalloonLayoutType_e.swDetailingBalloonLayout_Right,
            _ => (int)swBalloonLayoutType_e.swDetailingBalloonLayout_Square,
        };
        var balloons = dwg.AutoBalloon3(layoutId, false,
            (int)swBalloonStyle_e.swBS_Circular,
            (int)swBalloonFit_e.swBF_2Chars,
            (int)swBalloonTextContent_e.swBalloonTextItemNumber, "",
            (int)swBalloonTextContent_e.swBalloonTextQuantity, "",
            "") as object[];
        return $"{balloons?.Length ?? 0} balloon(s) inserted ({layout}).";
    }

    [KernelFunction(nameof(InsertBomTable))]
    [Description("Insert a Bill of Materials table on the active drawing " +
        "(assembly drawings only). 'bomType' is TOP_LEVEL (default), " +
        "PARTS_ONLY, or INDENTED. Uses the default BoM template unless " +
        "'templatePath' points to a custom .sldbomtbt file.")]
    public string InsertBomTable(
        string bomType = "TOP_LEVEL",
        string templatePath = "",
        string configurationName = "")
    {
        var dwg = RequireDrawing();
        var doc = (IModelDoc2)dwg;

        var type = bomType?.Trim().ToUpperInvariant() switch
        {
            "PARTS_ONLY" or "PARTSONLY" => swBomType_e.swBomType_PartsOnly,
            "INDENTED" => swBomType_e.swBomType_Indented,
            _ => swBomType_e.swBomType_TopLevelOnly,
        };

        var template = templatePath ?? string.Empty;
        var table = doc.Extension.InsertBomTable3(
            TemplateName: template,
            X: 0, Y: 0,
            BomType: (int)type,
            ConfigurationName: configurationName ?? string.Empty,
            Hidden: false,
            IndentedNumberingType: 0,
            DetailedCutList: false)
            ?? throw new InvalidOperationException(
                "InsertBomTable3 returned null. Ensure this is an assembly " +
                "drawing and the template path is valid (if provided).");
        return $"BoM table inserted ({type}).";
    }

    private IDrawingDoc RequireDrawing()
    {
        var doc = ActiveSwDoc
            ?? throw new InvalidOperationException("No active SolidWorks document.");
        if (doc is not IDrawingDoc dwg
            || (swDocumentTypes_e)doc.GetType() != swDocumentTypes_e.swDocDRAWING)
        {
            throw new InvalidOperationException(
                "This drawing skill requires an active drawing document.");
        }
        return dwg;
    }

    private static swDwgPaperSizes_e ParsePaperSize(string? sheetSize) =>
        sheetSize?.Trim().ToUpperInvariant() switch
        {
            null or "" or "A" or "ALETTER" => swDwgPaperSizes_e.swDwgPaperAsize,
            "A0" => swDwgPaperSizes_e.swDwgPaperA0size,
            "A1" => swDwgPaperSizes_e.swDwgPaperA1size,
            "A2" => swDwgPaperSizes_e.swDwgPaperA2size,
            "A3" => swDwgPaperSizes_e.swDwgPaperA3size,
            "A4" => swDwgPaperSizes_e.swDwgPaperA4size,
            "B" => swDwgPaperSizes_e.swDwgPaperBsize,
            "C" => swDwgPaperSizes_e.swDwgPaperCsize,
            "D" => swDwgPaperSizes_e.swDwgPaperDsize,
            "E" => swDwgPaperSizes_e.swDwgPaperEsize,
            "LETTER" => swDwgPaperSizes_e.swDwgPaperAsize,
            "LEGAL" => swDwgPaperSizes_e.swDwgPaperBsize,
            _ => swDwgPaperSizes_e.swDwgPaperAsize,
        };
}

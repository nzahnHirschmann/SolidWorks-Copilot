using Copilot.Sw.Extensions;
using Microsoft.SemanticKernel;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System.ComponentModel;
using System.Text.Json;

namespace Copilot.Sw.Skills.Core;

/// <summary>
/// Read-only introspection: tells the model where it is (which doc type,
/// which sketch, what's selected, what the units are). Without this every
/// other skill has to guess.
/// </summary>
public sealed class ContextQuerySkill : SldWorksSkillContext
{
    [KernelFunction(nameof(GetActiveContext))]
    [Description("Return a JSON snapshot of the current SolidWorks state: " +
        "active document type and title, active sketch name (if any), " +
        "active configuration, current selection count, and the document's " +
        "length unit. Always call this before invoking modelling skills if " +
        "you are unsure what the user is currently looking at.")]
    public string GetActiveContext()
    {
        var ctx = ISldWorksExtensions.GetSwCurrentContext();
        var doc = ActiveSwDoc;

        string? title = null;
        string? path = null;
        string? activeSketch = null;
        string? activeConfig = null;
        int selectionCount = 0;
        string? lengthUnit = null;
        string? docType = ctx.ToString();

        if (doc is not null)
        {
            title = doc.GetTitle();
            path = doc.GetPathName();
            activeConfig = doc.ConfigurationManager?.ActiveConfiguration?.Name;
            lengthUnit = SwUnits.GetDocumentLengthUnit(doc);

            var sketch = doc.SketchManager?.ActiveSketch as IFeature;
            activeSketch = sketch?.Name;

            var selMgr = doc.SelectionManager as ISelectionMgr;
            selectionCount = selMgr?.GetSelectedObjectCount2(-1) ?? 0;
        }

        var payload = new
        {
            isSolidWorksRunning = Sw is not null,
            docType,
            title,
            path,
            activeSketch,
            activeConfig,
            selectionCount,
            lengthUnit,
        };

        return JsonSerializer.Serialize(payload);
    }

    [KernelFunction(nameof(GetDocumentUnits))]
    [Description("Return the active document's length unit (mm, cm, m, in, ft). " +
        "Modelling skills accept values in this unit by default.")]
    public string GetDocumentUnits() => SwUnits.GetDocumentLengthUnit(ActiveSwDoc);
}

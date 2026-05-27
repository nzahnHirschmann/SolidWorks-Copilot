using Microsoft.SemanticKernel;
using SolidWorks.Interop.swconst;
using System;
using System.ComponentModel;

namespace Copilot.Sw.Skills.SolidWorksSkill;

public class DocumentCreationSkill : SldWorksSkillContext
{
    [KernelFunction(nameof(CreatePart))]
    [Description("Create a new SolidWorks part document.")]
    public void CreatePart()
    {
        Sw?.NewPart();
    }

    [KernelFunction(nameof(CreateAssembly))]
    [Description("Create a new SolidWorks assembly document.")]
    public void CreateAssembly()
    {
        Sw?.NewAssembly();
    }

    [KernelFunction(nameof(CreateDrawing))]
    [Description("Create a new SolidWorks drawing document using the user's " +
        "default drawing template.")]
    public void CreateDrawing()
    {
        if (Sw is null)
        {
            throw new InvalidOperationException("SolidWorks is not running.");
        }

        // SolidWorks 2018+ provides NewDocument(template, paperSize, w, h);
        // pull the user's configured drawing template so this respects
        // company standards instead of forcing a default sheet size.
        var template = Sw.GetUserPreferenceStringValue(
            (int)swUserPreferenceStringValue_e.swDefaultTemplateDrawing);
        if (string.IsNullOrWhiteSpace(template))
        {
            throw new InvalidOperationException(
                "No default drawing template is configured in SolidWorks " +
                "(Options → Default Templates → Drawing).");
        }

        var doc = Sw.NewDocument(
            template,
            (int)swDwgPaperSizes_e.swDwgPaperAsize,
            0.0,
            0.0);
        if (doc is null)
        {
            throw new InvalidOperationException(
                $"Failed to create drawing from template '{template}'.");
        }
    }
}


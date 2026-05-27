using Microsoft.SemanticKernel;
using System.ComponentModel;

namespace Copilot.Sw.Skills.SolidWorksSkill;

public class DocumentCreationSkill : SldWorksSkillContext
{
    [KernelFunction(nameof(CreatePart))]
    [Description("Create a new SolidWorks part document.")]
    public void CreatePart()
    {
        Sw.NewPart();
    }

    [KernelFunction(nameof(CreateAssembly))]
    [Description("Create a new SolidWorks assembly document.")]
    public void CreateAssembly()
    {
        Sw.NewAssembly();
    }

    [KernelFunction(nameof(CreateDrawing))]
    [Description("Create a new SolidWorks drawing document.")]
    public void CreateDrawing()
    {
    }
}

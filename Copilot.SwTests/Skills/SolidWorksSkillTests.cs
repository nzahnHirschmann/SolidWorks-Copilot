using Copilot.Sw.Skills;
using Microsoft.SemanticKernel;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Copilot.SwTests.Skills;

[TestClass]
public class SolidWorksPlanSkillTests : SkillTestbase
{
    [TestMethod, Ignore("Requires a running SolidWorks instance + live LLM credentials.")]
    public async Task CreateTest()
    {
        var planSkill = new SolidWorksPlanSkill(Kernel);
        var reply = await planSkill.ChatAsync("什么是机械工程?", history: "", default);

        Console.WriteLine(reply);
        Assert.AreNotEqual("Drawing", reply);
    }

    [TestMethod, Ignore("Requires a running SolidWorks instance + live LLM credentials.")]
    public async Task TaskTest()
    {
        var planSkill = new SolidWorksPlanSkill(Kernel);
        var reply = await planSkill.ChatAsync("草图中绘制三个圆形", history: "", default);

        Assert.IsTrue(reply.Contains("Nothing"));
    }
}

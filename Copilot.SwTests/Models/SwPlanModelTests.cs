using Copilot.Sw.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Copilot.Sw.Tests.Models;

[TestClass]
public class SwPlanModelTests
{
    [TestMethod]
    public void TryParse_Null_Returns_False()
    {
        Assert.IsFalse(SwPlanModel.TryParse(null!, out _));
    }

    [TestMethod]
    public void TryParse_Empty_Returns_False()
    {
        Assert.IsFalse(SwPlanModel.TryParse(string.Empty, out _));
    }

    [TestMethod]
    public void TryParse_Garbage_Returns_False()
    {
        Assert.IsFalse(SwPlanModel.TryParse("not xml at all", out _));
    }

    [TestMethod]
    public void TryParse_Single_Skill_Plan_Returns_True()
    {
        var xml = """
            <plan>
              <skill skillname="CreateCircle" goal="Draw circle r=5"/>
            </plan>
            """;

        Assert.IsTrue(SwPlanModel.TryParse(xml, out var model));
        Assert.AreEqual(1, model.ExecuteSkills.Count);
        Assert.AreEqual("CreateCircle", model.ExecuteSkills[0].SkillName);
        Assert.AreEqual("Draw circle r=5", model.ExecuteSkills[0].Description);
    }

    [TestMethod]
    public void TryParse_Multi_Skill_Plan_Returns_All_Skills_In_Order()
    {
        var xml = """
            <plan>
              <skill skillname="CreatePart"   goal="New part"/>
              <skill skillname="CreateCircle" goal="Draw circle"/>
              <skill skillname="Close"        goal="Close doc"/>
            </plan>
            """;

        Assert.IsTrue(SwPlanModel.TryParse(xml, out var model));
        Assert.AreEqual(3, model.ExecuteSkills.Count);
        Assert.AreEqual("CreatePart", model.ExecuteSkills[0].SkillName);
        Assert.AreEqual("Close", model.ExecuteSkills[2].SkillName);
    }
}

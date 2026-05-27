using Copilot.Sw.Models;
using Copilot.Sw.Skills;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Runtime.InteropServices;

namespace Copilot.Sw.P6Tests;

[TestClass]
public class SolidWorksSkillExceptionTests
{
    [TestMethod]
    public void Wrap_PassesThrough_WhenAlreadyNormalised()
    {
        var original = new SolidWorksSkillException("X", "msg");
        var actual = SolidWorksSkillException.Wrap(original, "op");
        Assert.AreSame(original, actual);
    }

    [TestMethod]
    public void Wrap_AssignsCode_FromCommonRuntimeTypes()
    {
        Assert.AreEqual("BAD_ARG",
            SolidWorksSkillException.Wrap(new System.ArgumentException("a"), "op").Code);
        Assert.AreEqual("BAD_STATE",
            SolidWorksSkillException.Wrap(new System.InvalidOperationException("b"), "op").Code);
        Assert.AreEqual("UNSUPPORTED",
            SolidWorksSkillException.Wrap(new System.NotSupportedException("c"), "op").Code);
        Assert.AreEqual("NOT_FOUND",
            SolidWorksSkillException.Wrap(new System.IO.FileNotFoundException("d"), "op").Code);
        Assert.AreEqual("DENIED",
            SolidWorksSkillException.Wrap(new System.UnauthorizedAccessException("e"), "op").Code);
    }

    [TestMethod]
    public void Wrap_FormatsComHResult()
    {
        var com = new COMException("hr", unchecked((int)0x80004005));
        var w = SolidWorksSkillException.Wrap(com, "Sw.Foo");
        StringAssert.StartsWith(w.Code, "SW_COM(0x");
        StringAssert.Contains(w.Message, "Sw.Foo");
        StringAssert.Contains(w.Message, "hr");
        Assert.AreSame(com, w.InnerException);
    }
}

[TestClass]
public class ConversationPreprocessorTests
{
    [TestMethod]
    public void ExpandSlashCommand_LeavesPlainTextAlone()
    {
        var input = "make a 30 mm box";
        Assert.AreEqual(input, Conversation.ExpandSlashCommand(input));
    }

    [TestMethod]
    public void ExpandSlashCommand_ReplacesKnownSlash()
    {
        var expanded = Conversation.ExpandSlashCommand("/help");
        Assert.AreNotEqual("/help", expanded);
        Assert.IsTrue(expanded.Length > "/help".Length);
    }

    [TestMethod]
    public void ExpandSlashCommand_KeepsUnknownSlash()
    {
        var input = "/totally-unknown-command foo";
        Assert.AreEqual(input, Conversation.ExpandSlashCommand(input));
    }
}

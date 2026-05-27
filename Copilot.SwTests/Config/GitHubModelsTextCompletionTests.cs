using Copilot.Sw.Config;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Copilot.Sw.Tests.Config;

[TestClass]
public class GitHubModelsTextCompletionTests
{
    [TestMethod]
    [ExpectedException(typeof(ArgumentException))]
    public void Ctor_Throws_When_Model_Missing()
    {
        _ = new GitHubModelsTextCompletion(
            GitHubModelsTextCompletion.DefaultEndpoint, "", "token");
    }

    [TestMethod]
    [ExpectedException(typeof(ArgumentException))]
    public void Ctor_Throws_When_Token_Missing()
    {
        _ = new GitHubModelsTextCompletion(
            GitHubModelsTextCompletion.DefaultEndpoint, "openai/gpt-4o-mini", "");
    }

    [TestMethod]
    public void Ctor_Accepts_Valid_Arguments()
    {
        var sut = new GitHubModelsTextCompletion(
            GitHubModelsTextCompletion.DefaultEndpoint,
            "openai/gpt-4o-mini",
            "fake-token");

        Assert.IsNotNull(sut);
    }
}

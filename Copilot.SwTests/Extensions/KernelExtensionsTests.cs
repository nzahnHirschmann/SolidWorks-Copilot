using Copilot.Sw.Config;
using Copilot.Sw.Extensions;
using Microsoft.SemanticKernel;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Copilot.Sw.Tests.Extensions;

[TestClass]
public class KernelExtensionsTests
{
    [TestMethod]
    public void LoadConfigs_With_Null_Returns_False()
    {
        var kernel = Kernel.Builder.Build();
        Assert.IsFalse(kernel.Config.LoadConfigs(null!));
    }

    [TestMethod]
    public void LoadConfigs_With_Empty_Returns_False()
    {
        var kernel = Kernel.Builder.Build();
        Assert.IsFalse(kernel.Config.LoadConfigs(new List<TextCompletionConfig>()));
    }

    [TestMethod]
    public void LoadConfigs_Honors_IsDefault_Flag()
    {
        var kernel = Kernel.Builder.Build();

        var configs = new List<TextCompletionConfig>
        {
            new() { Name = "alpha",  Type = ServerType.GitHubModels, Model = "openai/gpt-4o-mini", Apikey = "k1" },
            new() { Name = "bravo",  Type = ServerType.GitHubModels, Model = "openai/gpt-4o-mini", Apikey = "k2", IsDefault = true },
            new() { Name = "gamma",  Type = ServerType.GitHubModels, Model = "openai/gpt-4o-mini", Apikey = "k3" },
        };

        Assert.IsTrue(kernel.Config.LoadConfigs(configs));
        Assert.AreEqual("bravo", kernel.Config.DefaultTextCompletionServiceId);
    }

    [TestMethod]
    public void LoadConfigs_Falls_Back_To_First_When_No_IsDefault()
    {
        var kernel = Kernel.Builder.Build();

        var configs = new List<TextCompletionConfig>
        {
            new() { Name = "alpha", Type = ServerType.GitHubModels, Model = "openai/gpt-4o-mini", Apikey = "k1" },
            new() { Name = "bravo", Type = ServerType.GitHubModels, Model = "openai/gpt-4o-mini", Apikey = "k2" },
        };

        Assert.IsTrue(kernel.Config.LoadConfigs(configs));
        Assert.AreEqual("alpha", kernel.Config.DefaultTextCompletionServiceId);
    }
}

using Copilot.Sw.Config;
using Copilot.Sw.Extensions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Copilot.Sw.Tests.Extensions;

[TestClass]
public class KernelExtensionsTests
{
    [TestMethod]
    public void LoadConfigs_With_Null_Returns_False()
    {
        var builder = Kernel.CreateBuilder();
        Assert.IsFalse(builder.LoadConfigs(null!));
    }

    [TestMethod]
    public void LoadConfigs_With_Empty_Returns_False()
    {
        var builder = Kernel.CreateBuilder();
        Assert.IsFalse(builder.LoadConfigs(new List<TextCompletionConfig>()));
    }

    [TestMethod]
    public void LoadConfigs_Registers_Default_Service_Without_Id()
    {
        var builder = Kernel.CreateBuilder();

        var configs = new List<TextCompletionConfig>
        {
            new() { Name = "alpha", Type = ServerType.GitHubModels, Model = "openai/gpt-4o-mini", Apikey = "k1" },
            new() { Name = "bravo", Type = ServerType.GitHubModels, Model = "openai/gpt-4o-mini", Apikey = "k2", IsDefault = true },
            new() { Name = "gamma", Type = ServerType.GitHubModels, Model = "openai/gpt-4o-mini", Apikey = "k3" },
        };

        Assert.IsTrue(builder.LoadConfigs(configs));

        var kernel = builder.Build();

        // The IsDefault entry was registered without a serviceId, so the
        // un-keyed resolution returns it.
        var defaultService = kernel.GetRequiredService<IChatCompletionService>();
        Assert.IsNotNull(defaultService);

        // Non-default entries are still resolvable by id.
        var alpha = kernel.GetRequiredService<IChatCompletionService>(serviceKey: "alpha");
        var gamma = kernel.GetRequiredService<IChatCompletionService>(serviceKey: "gamma");
        Assert.IsNotNull(alpha);
        Assert.IsNotNull(gamma);
    }

    [TestMethod]
    public void LoadConfigs_Falls_Back_To_First_When_No_IsDefault()
    {
        var builder = Kernel.CreateBuilder();

        var configs = new List<TextCompletionConfig>
        {
            new() { Name = "alpha", Type = ServerType.GitHubModels, Model = "openai/gpt-4o-mini", Apikey = "k1" },
            new() { Name = "bravo", Type = ServerType.GitHubModels, Model = "openai/gpt-4o-mini", Apikey = "k2" },
        };

        Assert.IsTrue(builder.LoadConfigs(configs));

        var kernel = builder.Build();

        Assert.IsNotNull(kernel.GetRequiredService<IChatCompletionService>());
        Assert.IsNotNull(kernel.GetRequiredService<IChatCompletionService>(serviceKey: "bravo"));
    }
}

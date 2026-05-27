using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Copilot.Sw.Config;
using Copilot.Sw.Extensions;
using Copilot.Sw.Skills;

namespace Copilot.Sw.ViewModels.Tests;

[TestClass()]
public class KernelTests
{
    /// <summary>
    /// Requires a real OpenAI API key in <c>%APPDATA%\SolidWorks Copilot\settings.json</c>.
    /// Skipped on CI / fresh machines.
    /// </summary>
    [TestMethod, Ignore("Requires live OpenAI credentials.")]
    public async Task QuestionTest()
    {
        var configs = new TextCompletionProvider().Load();
        if (configs is null || configs.Count == 0)
        {
            Assert.Inconclusive("Config your Api key");
            return;
        }

        var builder = Kernel.CreateBuilder();
        builder.LoadConfigs(configs);
        var kernel = builder.Build();

        var chat = kernel.GetRequiredService<IChatCompletionService>();
        var history = new ChatHistory();
        history.AddUserMessage("Tell me something about SolidWorks");

        var result = await chat.GetChatMessageContentAsync(history, kernel: kernel);
        Console.WriteLine(result.Content);
    }
}
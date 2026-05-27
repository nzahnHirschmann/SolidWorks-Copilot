using Copilot.Sw.Skills;
using Copilot.Sw.Skills.SketchSkill;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Copilot.SwTests.Skills;

[TestClass]
public class CreateSketchSegmentSkillTest
{
    [TestMethod, Ignore("Requires a running SolidWorks instance + live LLM credentials.")]
    public async Task Test()
    {
        var kernel = StandandAloneSw.S_Instance.InitKernel();
        kernel.Plugins.AddFromObject(new SketchSegmentCreationSkill(), nameof(SketchSegmentCreationSkill));

        var chat = kernel.GetRequiredService<IChatCompletionService>();
        var history = new ChatHistory();
        history.AddUserMessage("我需要在SolidWorks草图中绘制10个直径为100的圆，竖直排列，间距10");

        var settings = new OpenAIPromptExecutionSettings
        {
            FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(),
        };
        var result = await chat.GetChatMessageContentAsync(history, settings, kernel);
        Console.WriteLine(result.Content);
    }
}

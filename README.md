# SolidWorks-Copilot

<div align="center">
    <img src="./Copilot.Sw/Assets/Icons/SolidWorksCopilot.png"/>
</div>

**In development**

Your SolidWorks Copoilt  base on LLM(ChatGPT)

# [Semantic Kernel](https://github.com/microsoft/semantic-kernel)

SolidWorks Copilot uses Semantic Kernel to converse with the LLM model and direct the SolidWorks API.

[promptingguide](https://www.promptingguide.ai/zh)

## Supported LLM backends

The add-in can be pointed at several OpenAI-compatible providers from the
**Settings** dialog (LLM Server tab). Pick the type that matches your account:

| Type            | Endpoint / Notes                                       | `Model` field example          | `Api key` field          |
| --------------- | ------------------------------------------------------ | ------------------------------ | ------------------------ |
| `OpenAI`        | api.openai.com                                          | `gpt-3.5-turbo-instruct`       | OpenAI secret key        |
| `Azure`         | Your Azure OpenAI resource endpoint                     | Your deployment name           | Azure OpenAI key         |
| `GitHubModels`  | `https://models.github.ai/inference` (default)          | `openai/gpt-4o-mini`, `openai/gpt-4o`, `meta/Llama-3.3-70B-Instruct`, … | GitHub PAT with `models:read` |

### Using GitHub Copilot / GitHub Models

GitHub Copilot's chat models are exposed publicly through the **GitHub Models**
inference API, which is OpenAI-compatible. To use it from SolidWorks Copilot:

1. Create a GitHub fine-grained Personal Access Token with the
   **`models:read`** permission at <https://github.com/settings/personal-access-tokens>.
2. In SolidWorks open the Copilot pane → ⚙ **Settings** → add a new server.
3. Set:
   - **Type:** `GitHubModels`
   - **Endpoint:** `https://models.github.ai/inference` (auto-filled)
   - **Model:** any catalog id, e.g. `openai/gpt-4o-mini`
   - **Api key:** the PAT from step 1
4. Click **Set as default** → **Ok**.

The full model catalog is available at
<https://github.com/marketplace?type=models>. Since GitHub Models exposes only
chat completions, the add-in transparently adapts each Semantic Kernel
text-completion prompt into a single-turn chat request
(see `Copilot.Sw/Config/GitHubModelsTextCompletion.cs`).

> ⚠️ The settings file (`%APPDATA%\Copilot.Sw\settings.json`) stores tokens in
> plain text — treat that file like any other secret.

# Copilot

<div align="center">
    <img src="./Assets/preview.png" width="500"/>
</div>

# Skills

**Building...**

SolidWorks has different operational contexts, and in order for LLM to better participate in these contexts, the following workspaces have been temporarily divided for SolidWorks, and AI Skills and Native Function have been created for these workspaces.

1. Document
2. Sketch
3. Feature
4. Property
5. Assembly

# UI Resources

[MasterGo](https://mastergo.com/goto/pBSvsRy9?file=90150584484334)

# Next

> Try to use other pretrained model

Add the constraints - [SketchGraphs](https://github.com/PrincetonLIPS/SketchGraphs)

Add the mates between components - [JoinABLe](https://github.com/AutodeskAILab/JoinABLe)
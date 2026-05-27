# SolidWorks-Copilot — Audit & Improvement Plan

_Last updated: 2026-05-27_

This document is a walk-through of the codebase, the issues it has today, and a prioritized improvement plan. It is meant as the onboarding map for anyone (human or agent) returning to this repo.

---

## 1. What the app is

A SolidWorks add-in (.NET Framework 4.8, COM-visible) that puts an LLM chat pane inside SolidWorks. The chat can answer questions and — via Semantic Kernel "skills" — drive SolidWorks itself (create sketches, parts, etc.) through COM interop.

Three LLM backends are supported via a provider abstraction: **OpenAI**, **Azure OpenAI**, and **GitHub Models** (the last added recently with a 3-step PAT sign-in flow).

---

## 2. Architecture map

```
SolidWorks (COM STA)
└── AddIn  (Copilot.Sw/AddIn.cs)              ◄── COM entry, [Guid], [ComVisible]
    ├── DI: ServiceCollection           ── AddIn.Services
    ├── DI: Ioc.Default (CommunityToolkit) ── second container ⚠ duplicate
    │
    ├── UI
    │   ├── WPFChatPane   (task pane)           ◄── ACTIVE
    │   ├── QuickChatPane (floating window)     ◄── ACTIVE
    │   ├── SettingsWindow                       ◄── ACTIVE (3-step GitHub sign-in)
    │   └── ChatPane (WebView2)                  ◄── DEAD / legacy
    │
    ├── ViewModels
    │   ├── WPFChatPaneViewModel    → BuildKernel() / SendAsync()
    │   ├── QuickChatPaneViewModel  : WPFChatPaneViewModel  (duplicates SendAsync)
    │   └── SettingsWindowViewModel → SignInWithGitHubAsync(), Save()
    │
    ├── Skills (Semantic Kernel 0.13-preview)
    │   ├── SkillsProvider          → scans Skills/*Skill/ on disk
    │   ├── SolidWorksPlanSkill     → 2-stage: classify → plan|chat
    │   ├── SwSkillSelection        → static-cached planner prompt ⚠
    │   ├── Semantic prompts (skprompt.txt)
    │   │   ├── SketchSkill/CreateSketchSegment   ⚠ encoding corrupt
    │   │   ├── SketchSkill/CreateCircle
    │   │   └── SolidWorksSkill/CreateDocument    (stub)
    │   └── Native C# skills (NOT imported into kernel ⚠)
    │       ├── SketchSegmentCreationSkill
    │       └── DocumentCreatationSkill
    │
    ├── Config (LLM providers)
    │   ├── ITextCompletionProvider / TextCompletionProvider
    │   │      └── %APPDATA%\SolidWorks Copilot\settings.json  (plaintext ⚠)
    │   ├── TextCompletionConfig  (ServerType: OpenAI|Azure|GitHubModels)
    │   ├── GitHubModelsTextCompletion (custom ITextCompletion → HTTP)
    │   └── KernelExtensions.LoadConfigs() — wires SK services
    │
    └── COM interop
        ├── ISldWorksExtensions.GetSwCurrentContext()
        └── SldWorksSkillContext / SwWorkingContext / SwSkillSelection
```

**Wiring chain on a single user message**

`AddIn.OnConnect` → creates `WPFChatPane` (task pane) → ctor pulls `WPFChatPaneViewModel` from `Ioc.Default` → `Init()` → `BuildKernel()` (synchronous I/O on UI thread ⚠).
User sends text → `SendCommand` → `Conversation.ChatAsync()` → instantiates `SolidWorksPlanSkill` fresh per message → SK classifies → plan or chat.

---

## 3. Top issues (ranked)

### Critical — correctness / behavior

| # | Where | Issue |
|---|---|---|
| C1 | `AddIn.cs` ~L50 | **Two DI containers built.** `Services` and `Ioc.Default` get separate `ServiceProvider` instances → singletons exist twice; `WPFChatPane` and `AddIn_CommandClick` see different objects. |
| C2 | `SolidWorksPlanSkill.ChatWithSolidWorksAsync` | **Native skills never imported.** Planner runs against a kernel with no callable functions; SW-mutating plans cannot execute. |
| C3 | `KernelExtensions.LoadConfigs()` ~L59 | **`IsDefault` ignored.** `SetDefaultTextCompletionService(configs.First().Name)` — user choice in settings is silently discarded. |
| C4 | `Skills/SketchSkill/CreateSketchSegment/skprompt.txt` | **File encoding corrupted** (mojibake). Prompt is garbage at runtime. |
| C5 | `SwSkillSelection._skillBuilder` | **Static cache** for planner skill list — never invalidated; never reflects new/edited skills until process restart. |

### Significant — quality / safety

| # | Where | Issue |
|---|---|---|
| S1 | `TextCompletionProvider` → settings.json | **PAT / API keys stored in plaintext** in `%APPDATA%`. README warns but nothing mitigates. |
| S2 | `WPFChatPane` / `QuickChatPane` ctors | **`Init()` runs sync on UI thread.** Disk I/O + SK init blocks SolidWorks' STA thread. |
| S3 | `QuickChatPaneViewModel.SendAsync` | **Verbatim duplication** of base class — should `base.SendAsync()` or be deleted. |
| S4 | `ChatPane.xaml`(.cs) | **Dead WebView2 pane** still in tree; pulls `Microsoft.Web.WebView2` reference for nothing. |
| S5 | `SkillsProvider.GetSkills()` L34 | `model.Index = 1;` — bug: index never increments. |
| S6 | `SolidWorksPlanSkill` ctor | `_taskPlanFunc` is `[Obsolete]` but still built on every instance → wasted token cost on every message. |
| S7 | `KernelExtensions.LoadConfigs()` | Hardcodes `text-embedding-ada-002` for every OpenAI config; embedding is never actually used in chat. Will 4xx for accounts without that model. |

### Minor — papercuts

| # | Where | Issue |
|---|---|---|
| M1 | `ITextCompletionProvider.Wirte()` | Typo on interface method → public API wart. |
| M2 | `Skills/SolidWorksSkill/DocumentCreatationSkill.cs` | Typo (Creataion); also `CreateDrawing()` is empty; `Setting()` lacks `[SKFunction]`. |
| M3 | `Converters/EnumToItemsConveter.cs` | Typo in filename. |
| M4 | `Skills/SketchSkill/SketchSegmentCreationSkill.SketchLevelPlan()` | Empty `[SKFunction]` exposed to kernel. |
| M5 | `Properties/launchSettings.json` | Hardcoded `C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\SLDWORKS.exe`. Non-portable. |
| M6 | `Copilot.SwTests/Config/TextCompletionProviderTests.ConfigTest` | Writes to the real `%APPDATA%` settings file — can clobber dev config. |
| M7 | `GitHubModelsTextCompletion` | Dead `#if NET7_0_OR_GREATER` branch (NET48-only project); cancellation token dropped on NET48. |
| M8 | `SemanticKernel 0.13.277.1-preview` | Two years stale; SK 1.x has rewritten `ITextCompletion`/planner APIs. Blocks newer-model support. |

---

## 4. UI inventory

| File | Status |
|---|---|
| [Copilot.Sw/WPFChatPane.xaml](../Copilot.Sw/WPFChatPane.xaml) + `.cs` | Active task pane |
| [Copilot.Sw/Views/QuickChatPane.xaml](../Copilot.Sw/Views/QuickChatPane.xaml) + `.cs` | Active floating window |
| [Copilot.Sw/Views/SettingsWindow.xaml](../Copilot.Sw/Views/SettingsWindow.xaml) + `.cs` | Active; hosts 3-step GitHub Models sign-in + Advanced editor |
| [Copilot.Sw/ChatPane.xaml](../Copilot.Sw/ChatPane.xaml) + `.cs` | **Dead** (WebView2 prototype) |

---

## 5. Tests today

| File | Tests | Reality |
|---|---|---|
| `Skills/SkillsProviderTests.cs` | `GetSkills` non-null | OK |
| `Skills/CreateSketchSegmentSkillTest.cs` | Live LLM call | needs real key; planner test commented out |
| `Skills/SolidWorksSkillTests.cs` | classify + plan | live LLM; brittle string assertion `"Nothing"` |
| `Config/TextCompletionProviderTests.cs` | load/write | clobbers real settings file; one method returns instead of asserts |

**Zero coverage:** `GitHubModelsTextCompletion`, `KernelExtensions.LoadConfigs`, `SettingsWindowViewModel`, `SwPlanModel.TryParse`, `SkillsParse.Parse`, all native skill classes.

---

## 6. Improvement plan

Effort key: **S** ≈ <½ day · **M** ≈ 1–2 days · **L** ≈ ≥1 week.

### Phase 1 — Correctness (must-fix; mostly small)

- [ ] **P1.1 (S)** Merge the two DI containers. Build once; assign the same `IServiceProvider` to both `AddIn.Services` and `Ioc.Default`.
- [ ] **P1.2 (S)** Honor `IsDefault` in `KernelExtensions.LoadConfigs()` — select the flagged config (fallback to first).
- [ ] **P1.3 (S)** Re-save `Skills/SketchSkill/CreateSketchSegment/skprompt.txt` as UTF-8 with intended English/Chinese content; verify SK reads the right bytes.
- [ ] **P1.4 (S)** Import the native skill classes (`SketchSegmentCreationSkill`, `DocumentCreatationSkill`) into the kernel before `SequentialPlanner.CreatePlanAsync` runs — otherwise plans are unreachable.
- [ ] **P1.5 (S)** Fix `SkillsProvider.GetSkills()` indexing (`model.Index = index++`).
- [ ] **P1.6 (S)** Drop the `[Obsolete]` `_taskPlanFunc` construction from `SolidWorksPlanSkill` ctor (saves a token call per message).
- [ ] **P1.7 (S)** Remove the hardcoded `text-embedding-ada-002` registration (or guard it behind a config flag) so accounts without that model don't 4xx.

### Phase 2 — Cleanup (high-leverage hygiene)

- [ ] **P2.1 (S)** Delete `ChatPane.xaml(.cs)` and drop the `Microsoft.Web.WebView2` package.
- [ ] **P2.2 (S)** `QuickChatPaneViewModel.SendAsync` → `base.SendAsync()` (or delete override).
- [ ] **P2.3 (S)** Make `_skillBuilder` in `SwSkillSelection` instance-scoped (or invalidate on provider change).
- [ ] **P2.4 (S)** Rename API typos in one pass: `ITextCompletionProvider.Wirte` → `Write`; `DocumentCreatationSkill` → `DocumentCreationSkill`; `EnumToItemsConveter.cs` → `EnumToItemsConverter.cs`.
- [ ] **P2.5 (S)** Remove empty `SketchLevelPlan()` `[SKFunction]`.
- [ ] **P2.6 (S)** Drop the `#if NET7_0_OR_GREATER` branch in `GitHubModelsTextCompletion`; thread cancellation through on NET48 via `HttpClient.SendAsync(req, ct)`.
- [ ] **P2.7 (S)** `launchSettings.json` — make the SW path overridable via env var or document the per-machine edit.

### Phase 3 — UX / responsiveness

- [ ] **P3.1 (S)** Move `BuildKernel()` off the UI thread: call `await viewModel.InitAsync()` from `Loaded`, not from the ctor. Show a spinner/disable input until ready.
- [ ] **P3.2 (M)** Surface kernel-init errors in the chat pane (currently swallowed when the first kernel build fails).
- [ ] **P3.3 (M)** Settings: validate PAT against `api.github.com/user` _after_ each edit; show inline error states.

### Phase 4 — Security

- [ ] **P4.1 (M)** Encrypt `Apikey`/PAT at rest using DPAPI (`ProtectedData.Protect`, `DataProtectionScope.CurrentUser`). Read-time decrypt; remain backward-compatible with plaintext for one release.
- [ ] **P4.2 (S)** Set `.gitignore` / docs to make absolutely sure no settings file ever lands in the repo.

### Phase 5 — Tests

- [ ] **P5.1 (M)** Add a unit test for `GitHubModelsTextCompletion` using a mock `HttpMessageHandler` (assert headers, body shape, choice/text parsing fallback).
- [ ] **P5.2 (M)** Add tests for `KernelExtensions.LoadConfigs()` covering: default selection, OpenAI vs Azure vs GitHubModels branches, empty config list.
- [ ] **P5.3 (S)** Redirect `TextCompletionProviderTests` to a temp folder so it doesn't clobber `%APPDATA%`.
- [ ] **P5.4 (S)** Replace brittle `"Nothing"` assertion in `SolidWorksSkillTests` with a structural one.
- [ ] **P5.5 (M)** Add `SwPlanModel.TryParse` and `SkillsParse.Parse` unit tests (parser is the load-bearing piece between LLM output and COM calls).

### Phase 6 — Long horizon

- [ ] **P6.1 (L)** Upgrade Semantic Kernel from `0.13.277.1-preview` to 1.x: replace `ITextCompletion`/`IChatCompletion`, replace `SequentialPlanner`, switch `[SKFunction]` attributes to `[KernelFunction]`. Likely requires reworking `SolidWorksPlanSkill` and the GitHub Models adapter.
- [ ] **P6.2 (L)** Move skill discovery to support both file-system prompts and native skills uniformly; expose a "Skills" panel in Settings.

---

## 7. Reading order for a newcomer

1. [Copilot.Sw/AddIn.cs](../Copilot.Sw/AddIn.cs) — how the add-in is registered and wired.
2. [Copilot.Sw/WPFChatPane.xaml.cs](../Copilot.Sw/WPFChatPane.xaml.cs) and [WPFChatPaneViewModel](../Copilot.Sw/ViewModels/WPFChatPaneViewModel.cs) — the main UI loop.
3. [Copilot.Sw/Models/Conversation.cs](../Copilot.Sw/Models/Conversation.cs) — the per-message orchestration.
4. [Copilot.Sw/Skills/SolidWorksPlanSkill.cs](../Copilot.Sw/Skills/SolidWorksPlanSkill.cs) — 2-stage classify → plan.
5. [Copilot.Sw/Extensions/KernelExtensions.cs](../Copilot.Sw/Extensions/KernelExtensions.cs) — provider → SK service mapping.
6. [Copilot.Sw/Config/](../Copilot.Sw/Config/) — provider abstraction + JSON persistence.
7. [Copilot.Sw/ViewModels/SettingsWindowViewModel.cs](../Copilot.Sw/ViewModels/SettingsWindowViewModel.cs) — GitHub Models sign-in flow.

---

## 8. Quick reference

- **Targets:** `net48` only. SolidWorks host is .NET Framework — do not retarget.
- **Registration:** `regasm /codebase Copilot.Sw.dll` (per recent commit; no more `EnableComHosting`/regsvr32).
- **Settings path:** `%APPDATA%\SolidWorks Copilot\settings.json`.
- **Debug:** F5 with `launchSettings.json` profile `"Sw"` (edit SLDWORKS path per machine).

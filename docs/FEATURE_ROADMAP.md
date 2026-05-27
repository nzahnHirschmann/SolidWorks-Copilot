# SolidWorks-Copilot — Feature Roadmap

_Last updated: 2026-05-27_

**Goal:** make it possible for a user to tell Copilot to *model anything in
CAD* and *check a drawing for accuracy*, fully driven from the chat pane.

This is the companion to [AUDIT.md](AUDIT.md). AUDIT covers existing bugs and
tech-debt; this document covers **net-new capability** that needs to land for
the headline use case to be real.

---

## 0. Current capability surface (baseline)

Functions actually exposed to the LLM today:

| Skill | File | Status |
|---|---|---|
| `CreatePart` | [DocumentCreationSkill.cs](../Copilot.Sw/Skills/SolidWorksSkill/DocumentCreationSkill.cs) | ✅ |
| `CreateAssembly` | same | ✅ |
| `CreateDrawing` | same | ⚠ **empty method body** |
| `CreateCircle` | [SketchSegmentCreationSkill.cs](../Copilot.Sw/Skills/SketchSkill/SketchSegmentCreationSkill.cs) | ✅ requires user to start the sketch first |

That is the entire LLM tool surface. Until the items below land, *“tell
Copilot to model anything”* is structurally impossible.

---

## 1. P0 — Foundation (blocks every other skill)

Without these, every higher-level skill reinvents the same plumbing or
breaks in the same way.

- [x] **P0.1 — Document lifecycle**: `OpenDocument(path)`, `SaveActive`,
  `SaveAs(path)`, `CloseActive`, `ListOpenDocuments`,
  `ActivateDocument(name)`.
- [x] **P0.2 — Active-context query**: wrap
  `ISldWorksExtensions.GetSwCurrentContext()` as
  `[KernelFunction] GetActiveContext()` returning
  `{ DocType, DocName, ActiveSketch?, ActiveFeature?, SelectionCount }`.
  Today the model is blind to whether it's in a part, sketch, or drawing.
- [x] **P0.3 — Sketch lifecycle**:
  `InsertSketchOnPlane("Front"|"Top"|"Right"|<face>)`, `ExitSketch`,
  `EditSketch(name)`. `CreateCircle` needs this — nothing currently starts
  a sketch.
- [x] **P0.4 — Selection skills**: `SelectByName(name, type)`,
  `SelectFaceAt(x,y,z)`, `ClearSelection`. Half the SW API is *“set
  selection, then call FeatureManager”*.
- [x] **P0.5 — Units & coordinates**: shared `Sw.Units` helper +
  `GetDocumentUnits()`. Move the hard-coded `/1000` mm→m hack out of
  `CreateCircle` so future skills can adapt to inch/mm docs.
- [x] **P0.6 — Auto-register native skills**: `KernelExtensions.BuildKernel`
  should discover every `SldWorksSkillContext` subclass (or
  `[CopilotSkill]`-marked type) and `AddFromType<>` it. Right now each
  skill has to be hand-wired in — which is how `CreateDrawing` ended up
  empty and unnoticed.
- [x] **P0.7 — Undo grouping**: wrap each multi-step chat turn in
  `IModelDocExtension.StartRecordingUndoObject` /
  `FinishRecordingUndoObject` so a botched plan can be reverted with one
  Ctrl-Z. Currently a 12-feature plan needs 50 undo clicks.

---

## 2. P1 — “Model anything” (Part modelling)

### 2.1 Sketch entities

- [x] `CreateLine(x1,y1,x2,y2)` / `CreatePolyline(points[])`
- [x] `CreateRectangle(x,y,w,h)` — corner + center variants
- [x] `CreateArc(...)` — 3-point and center+radius+sweep
- [x] `CreateSlot(...)` (straight + arc), `CreateEllipse(...)`, `CreatePoint(...)`
- [x] `CreatePolygon(...)` (bonus — was not in original plan)
- [x] `ToggleConstructionGeometry()` (acts on selection)
- [x] `SketchOffset(distance)` (offsets the current selection / chain)
- [x] `SketchTrim(x, y, trimType)`, `LinearSketchPattern`, `CircularSketchPattern`
- [x] `MirrorSketchEntities()` — pre-select entities + centre line
- [x] `AddRelation(type)` — Horizontal, Vertical, Coincident, Collinear,
  Concentric, Equal, Tangent, Perpendicular, Parallel, Fix, Midpoint,
  Symmetric (acts on selection)
- [x] `AddDimension(x, y, value?)` / smart-dimension at screen point

### 2.2 Reference geometry

- [x] **P1.2 — Reference plane (offset)** via
  `CreateOffsetPlane(sourcePlane, distance)`
- [x] `CreateAxis(...)`, `CreateCoordinateSystem(...)`, `CreateReferencePoint(...)`

### 2.3 Features — boss / cut

- [x] `Extrude(sketch, depth, direction, draft, thinWall)` and
  `ExtrudeCut(...)`
- [x] `Revolve(sketch, axis, angle)` / `RevolveCut(...)`
- [x] `Sweep(profileSketch, pathSketch)` / `SweepCut`
- [x] `Loft(profiles[])` / `LoftCut`
- [ ] `BoundarySurface(...)` — *deferred:* `IFeatureManager` in the
  shipped interop has no first-class `InsertBoundarySurface` /
  `InsertFilledSurface` entry point; authoring this feature would
  require driving the PMP page through `IModelDoc2.SketchManager` +
  `IModelDoc2.ClearSelection2` rituals that are too fragile for the
  agent loop. Keep open until a stable wrapper exists or until we
  ship a macro-recording shim.

### 2.4 Modify features

- [x] `Fillet(edges[], radius, type)`, `Chamfer(edges[], distance, angle)`
- [x] `Shell(thickness, outward)` (selection-based face removal)
- [x] `Draft(angleDegrees, flip)` (neutral plane + faces pre-selected)
- [x] `SimpleHole(diameter, depth, throughAll)` — basic hole
- [x] `InsertTappedHole(size, depth, standard, endCondition)` — HoleWizard5
  wrapper for the common tapped-hole case (ISO/DIN/ANSI/JIS/BSI/GB)
- [x] `InsertCounterBoreHole(size, depth, standard, endCondition)`,
  `InsertCounterSinkHole(...)`, `InsertPipeTapHole(...)` — all three
  wrap `IFeatureManager.HoleWizard5` with `swWzdGeneralHoleTypes_e`
  `swWzdCounterBore` / `swWzdCounterSink` / `swWzdPipeTap`, mapping a
  `standard` string (ISO/ANSI_METRIC/ANSI_INCH/DIN/JIS/BSI/GB) and the
  canonical fastener (socket-head cap screw / 82° flat head / tapered
  pipe tap) to the right `swWzdHoleStandardFastenerTypes_e` index.
- [x] `Thread(diameter, depth?, endCondition?, callout?)` — cosmetic
  thread via `InsertCosmeticThread3` on pre-selected cylindrical face
  or circular edge (BLIND / UP_TO_NEXT / THROUGH)
- [x] `LinearPattern`, `CircularPattern`, `MirrorFeature`

### 2.5 Body / surfacing

- [x] `CombineBodies(op)` — ADD / SUBTRACT / COMMON across all solid
  bodies (first body = main, rest = tools)
- [x] `MoveCopyBody(...)`, `DeleteBody(...)`
- [x] `SplitBody(consumeOriginal)` — wraps
  `IFeatureManager.PreSplitBody2` + `PostSplitBody2`. Pre-select the
  solid body followed by every trimming entity (planes / surfaces /
  sketches); all resulting bodies are marked for retention unless
  `consumeOriginal` is true.
- [x] `ThickenSurface(thickness, direction)`
- [x] `KnitSurfaces(mergeEntities?, knitToBody?, gapToleranceMm?)` — sew
  the pre-selected surfaces (`InsertSewRefSurface`); promote to solid if
  closed and `knitToBody` is true
- [x] `TrimSurface(mutualTrim, removePicked, sewSurface)` — wraps
  `IFeatureManager.PreTrimSurface` + `PostTrimSurface`. Standard trim
  (default) trims the pre-selected surface(s) against a trim tool;
  `mutualTrim=true` trims two intersecting surfaces against each other.

### 2.6 Configurations & equations

- [x] `AddConfiguration(name, comment?)`, `ActivateConfiguration(name)`,
  `ListConfigurations()`
- [x] `AddEquation("\"D1@Sketch1\" = \"D2@Sketch2\" * 2")` — global
  variables fall out of this naturally
- [x] `AddGlobalVariable(name, expression)` — typed wrapper that quotes
  the variable name automatically

### 2.7 Materials & mass

- [x] `SetMaterial(name)`
- [x] `GetMassProperties()` → mass, COG, volume, surface area, principal
  axes

---

## 3. P2 — Assembly modelling

- [x] `InsertComponent(path, x, y, z, configurationName?)` /
  `InsertComponentFromOpenDoc`
- [x] `FixSelectedComponent`, `FloatSelectedComponent` (selection-based)
- [x] `MoveComponent(name, dx, dy, dz)`, `DeleteComponent(name)`
- [x] **Mates** — `AddMate(type, distance?, angle?, alignment?, flip?)`:
  Coincident, Concentric, Parallel, Perpendicular, Tangent, Distance,
  Angle, Lock, Symmetric, Width (pre-select two refs)
- [x] Remaining mate types: **Cam, Gear, Slot, Path, Hinge, Screw**
  (plus UniversalJoint, RackPinion, LinearCoupler, ProfileCenter) —
  unified `AddMate(type, ..., gearRatioNumerator?, gearRatioDenominator?)`;
  pre-select the required references per mate type
- [x] `ReplaceComponent(newPath, replaceAllInstances, reAttachMates,
  configurationName)` — pre-select component instance
- [x] `SuppressSelectedComponent`, `ResolveSelectedComponent`
- [x] `ListComponents(topLevelOnly)`
- [x] `ListMates()`, `GetBoM(topLevelOnly)` — flat BoM with quantities
- [x] `ListExplodedViews()`, `ShowExplodedView(name, show)` — toggle
  pre-defined exploded views (configuration-scoped)
- [x] `CreateExplodedView(stepsJson)` — author a new exploded view on
  the active configuration. `stepsJson` is a JSON array of translational
  steps: `[{ "components": ["Bolt-1@Asm"], "axis": "X|Y|Z", "distanceMm": 50, "reverse": false }, …]`.
  Each step selects the listed components and calls
  `IConfiguration.AddExplodeStep2` with the requested axis (mapped via
  `swExplodeDirectionIndex_e`) and distance (mm → m). SolidWorks
  auto-names the resulting view; the call returns the new name plus the
  full list so the LLM can immediately `ShowExplodedView` it.
- [x] **Interference detection**: `RunInterferenceDetection()` →
  component pairs + volumes. This is half of *“check accuracy”* in
  assemblies.
- [x] `MeasureClearance()` — wraps `IModelDocExtension.CreateMeasure`
  on the two pre-selected entities (faces / edges / vertices /
  components); returns distance, centerDistance, normalDistance,
  delta XYZ (mm), plus isParallel / isPerpendicular / isIntersecting
- [x] `ForceRebuild`
- [x] `EvaluateMateErrors()`

---

## 4. P3 — Drawing creation & **accuracy checks**

### 4.1 Creation

- [x] `CreateDrawingFromPart(sheetSize?, insertModelDimensions?)` — uses
  the active part/assembly + the configured default template, drops a
  3rd-angle 3-view layout, optionally pulls model dimensions
- [x] `InsertNamedView(modelPath, viewName, x, y)` — any SW orientation
  name (`*Front`, `*Isometric`, …) or saved view
- [x] `InsertProjectedView(x, y, notAligned)` — from pre-selected parent
  view via `CreateUnfoldedViewAt3`
- [x] `InsertSectionView(x, y, label)` (pre-select section line) and
  `InsertDetailView(x, y, scale, label)` (pre-select detail circle)
- [x] `InsertAuxiliaryView(x, y, label)`
- [x] `InsertModelDimensions()` — bring driving dims into all views
- [x] `AddSheet(name, sheetSize, scale)`, `ActivateSheet(name)`
- [x] `InsertBomTable(bomType, templatePath, configurationName)` —
  PARTS_ONLY / TOP_LEVEL / INDENTED
- [x] `InsertHoleTable(x?, y?, tagStyle?, tagOrder?, combineSameSizes?, templatePath?)`,
  `InsertRevisionTable(x?, y?, tagStyle?, enableSymbolEnumeration?, templatePath?)`,
  `InsertWeldmentCutList(x?, y?, configurationName?, templatePath?)` —
  view/sheet-scoped (`IView.InsertHoleTable3` / `ISheet.InsertRevisionTable2` /
  `IView.InsertWeldmentTable`); (0,0) anchors automatically
- [x] `InsertCenterMarks(view)` (selection-driven, see 4.2),
  `InsertCenterlines()` (selection-driven, two parallel edges)
- [x] `AutoBalloonViews(layout)` — SQUARE / CIRCULAR / TOP / BOTTOM /
  LEFT / RIGHT

### 4.2 Annotations & GD&T

- [x] `AddDimension(x, y, value?)` — driving (sketch) or driven
  (drawing) dimension on the pre-selected entity / pair via
  `IModelDoc2.AddDimension2`; same function works in both doc types
- [x] `AddDatumFeature(letter)` — pre-select face/edge
- [x] `AddGeometricTolerance(symbol, tolerance, datumA, datumB, datumC)`
  — pre-select face/edge; symbols POSITION, FLATNESS, STRAIGHTNESS,
  PERPENDICULARITY, PARALLELISM, ANGULARITY, CIRCULARITY, CYLINDRICITY,
  PROFILE_LINE/SURFACE, RUNOUT, TOTAL_RUNOUT, SYMMETRY, CONCENTRICITY
- [x] `AddSurfaceFinish(maxRoughness, symbolType)` — BASIC / MACHINING /
  DONT_MACHINE; pre-select face/edge
- [x] `AddWeldSymbol(symbol, size, lengthPitch, fieldWeld, peripheral,
  symmetric)` — pre-select edge/face; SW symbol codes (e.g. `JWMA0102`
  fillet)
- [x] `AddNote(text, x, y)`

### 4.3 **Accuracy / QA checks** (the unique value-add)

Implement as discrete read-only skills, then aggregate behind one
`[KernelFunction] InspectDrawing()` that returns a structured report.

- [x] **InspectDrawing()** aggregate — JSON `{ summary, findings }`
  covering sheet/view inventory, scale consistency per sheet, title
  block completeness, feature-tree errors on each referenced model,
  missing referenced files, views without annotations.
- [x] **Feature-tree errors** — `ListFeatureTreeErrors()` returns every
  feature with a non-zero error code (with type + isWarning).
- [x] **Title block completeness** — `CheckTitleBlock(required?)`
  reading custom properties (defaults to PartNumber/Description/Material/
  Finish/Revision/DrawnBy/Date).
- [x] **Reference checks** — broken view references (model moved/
  renamed) surfaced via `missingReferences`.
- [x] **Sheet/view inventory** — `ListSheets()`, `ListDrawingViews()`.
- [x] **Missing dimensions** — `CheckMissingDimensions(minRatio)` flags
  views with zero dimensions or dim/edge ratio below the threshold.
- [x] **GD&T completeness** — `CheckGdtConsistency()` cross-checks
  defined datum-feature letters against datum letters referenced inside
  GTol frames; reports missing definitions.
- [x] **Tolerance sanity** — `CheckToleranceSanity()` walks every
  display dimension and reports counts by tolerance type plus the list
  of untoleranced dimensions.
- [x] **BoM vs assembly diff** — `CheckBomVsAssembly()` compares the
  first BoM table on the drawing to the live `ListComponents()` of its
  linked (open) assembly, flagging extra / missing / qty-mismatch rows.
- [x] **Standards compliance** — `RunDesignChecker(standardsFile)`
  best-effort wrapper around the SwDesignCheck addin (loads via CLSID
  `{59F38FA7-1FAC-4ED6-A5B9-5D1B7DD0FD4D}`, then reflection-invokes the
  documented entry points). Falls back with a clear error pointing at
  `InspectDrawing` when the addin is missing or its surface differs.
- [x] **Spell check** — `CheckSpelling()` collects every textual string
  (notes, dimension prefix/suffix/above/below, weld text, GTol below-
  frame text, sheet names, custom-property values) so the LLM can
  spell-check them itself. SolidWorks' native `IAnnotation.CheckSpelling`
  pops an interactive dialog, so we delegate to the model instead.

---

## 5. P4 — Querying & introspection

The model can't intelligently extend an existing part without being able
to *read* it.

- [x] `GetFeatureTree(maxDepth)` — JSON of feature names, types,
  suppression state, children
- [x] `GetSelectedEntity()` →
  `{ type, typeId, name, featureType }`
- [x] `GetSketchEntities(sketchName)` — segments (type/name/length/
  construction) + points (mm) for the active or named sketch
- [x] `GetReferenceGeometry()` — lists RefPlane / RefAxis / RefPoint /
  CoordSys features
- [x] `GetCustomProperties()` / `SetCustomProperty(name, value, type)`
- [x] `MeasureSelection()` — length / distance / angle / area / radius /
  delta XYZ from the current 1–2 selection (covers `MeasureDistance` and
  `MeasureAngle`)
- [x] `MeasureMinRadius()` — pre-select a face; uses
  `ISurface.FindMinimumRadius`, returns radius (mm) + 3D location
- [x] `GetBoundingBox()` — axis-aligned, part docs
- [x] `Screenshot(path, width, height)` → saves PNG/BMP/JPG and returns
  the path. Lets a vision-capable model literally *see* the part.

---

## 6. P5 — Multi-step authoring (LLM-native UX)

Not new SW APIs — agent-loop features.

- [ ] **Plan preview & confirm** — show the proposed sequence of skill
  calls before executing; user clicks *Apply* or *Edit*.
  `ActionAnswerMessage` already half-exists
  ([SwPlanModel.cs](../Copilot.Sw/Models/SwPlanModel.cs)); wire it back
  into the streaming loop so the model can output a structured plan
  *instead of* (or in addition to) auto-calling.
  **Partial:** `/plan` slash command instructs the model to emit the
  XML plan format, which `Conversation.ChatAsync` already routes to
  `ActionAnswerMessage`. Still TODO: explicit *Apply* / *Edit* buttons
  on the action bubble that re-issue the plan with auto tool-calling.
- [x] **Per-step status + rollback** — every native KernelFunction the
  model invokes is captured by
  [ToolCallTraceFilter.cs](../Copilot.Sw/Skills/ToolCallTraceFilter.cs)
  and surfaced under the assistant bubble as a live list of
  `✓ name (ms)` / `✗ name — error` chips, with arguments on hover.
  Rollback is delivered by the existing per-turn undo group in
  [Conversation.cs](../Copilot.Sw/Models/Conversation.cs) — one Ctrl-Z
  reverts everything the model did this turn.
- [x] **Dry-run mode** — `IsDryRun` toggle in the chat-pane header
  flips `FunctionChoiceBehavior` from `Auto()` to `None()` and adds a
  system-prompt suffix instructing the model to produce a numbered
  markdown plan instead of mutating the document. See
  [SolidWorksPlanSkill.cs](../Copilot.Sw/Skills/SolidWorksPlanSkill.cs).
  *Future:* cloned-doc execution with before/after screenshots.
- [ ] **Reference attachments** — drag a sketch image / PDF / DXF into
  chat; pass it to a vision model; have it produce a sketch plan.
  *Blocked on:* chat pane drag-drop infra + a configured vision model
  on the active provider.
- [x] **Templates / macros library** — named procedures stored in
  `%APPDATA%\Copilot.Sw\templates.json` (auto-materialised with
  defaults: `flange-bolt-circle`, `linear-bracket`,
  `drawing-from-active`, `mate-stack`, `inspect-drawing`). Exposed via
  [TemplatesSkillContext.cs](../Copilot.Sw/Skills/TemplatesSkill/TemplatesSkillContext.cs)
  KernelFunctions `ListTemplates` / `GetTemplate(name)`, and via the
  `/templates` and `/template <name>` slash commands.
- [x] **@-mentions in chat** — `@active`, `@selection`, `@sheet`,
  `@components`, `@features` are expanded by
  `Conversation.ExpandAtMentions` to an inline context snapshot the
  model can act on. Unknown tokens pass through unchanged.
- [x] **Slash commands** — `/inspect-drawing`, `/check-mates`,
  `/mass-props`, `/context`, `/screenshot`, `/feature-tree`, `/bom`,
  `/new-part`, `/new-assembly`, `/new-drawing`, `/rebuild`, `/help`
  are expanded by `Conversation.ExpandSlashCommand` to canonical
  natural-language prompts that map cleanly to KernelFunctions.

---

## 7. P6 — Productionisation

- [x] **Skill telemetry** — `ToolCallTraceFilter` captures every call
  into a per-turn `AsyncLocal` list for the chat UI; `GovernanceFilter`
  additionally appends a JSONL record (timestamp, plugin, function,
  args, durationMs, outcome, error code) to
  `%APPDATA%\Copilot.Sw\telemetry\YYYY-MM-DD.jsonl`.
- [x] **Permissioned tools** — `[RequiresConfirmation("title")]` gates
  any KernelFunction through `IConfirmationPrompt` (default: WPF
  `MessageBox` on the dispatcher thread; falls back to *deny* in
  headless contexts). Applied to `DeleteComponent`, `DeleteBody`,
  `CloseActiveDocument`, `SaveActiveDocumentAs`. A "no" raises
  `OperationCanceledException` which the trace filter records as
  `CANCELLED`.
- [x] **Error normalisation** — `SolidWorksSkillException(code,
  message, recoverable, inner)` with `Wrap(ex, operation)` mapping
  `ArgumentException → BAD_ARG`, `InvalidOperationException →
  BAD_STATE`, `NotSupportedException → UNSUPPORTED`,
  `FileNotFoundException → NOT_FOUND`,
  `UnauthorizedAccessException → DENIED`, `COMException →
  SW_COM(0x…)`. Applied uniformly by `GovernanceFilter` so raw COM
  HRESULTs never reach the model.
- [x] **Unit tests** — `Copilot.SwTests/Skills/P6Tests.cs` covers
  `SolidWorksSkillException.Wrap` mapping and
  `Conversation.ExpandSlashCommand` preprocessor behaviour. Mock
  `ISldWorks` harness via `StandardAloneSw` remains the path for
  per-skill tests as new skills land.

---

## 8. Suggested first wave

Minimum to move user perception from *demo toy* → *useful copilot*, in
this order:

1. **P0.2** `GetActiveContext` + **P0.3** sketch lifecycle + **P0.4**
   selection + **P0.6** auto-registration of native skills (unblocks the
   planner)
2. **P1.1** sketch entities (line, rectangle, arc) + **P1.3** `Extrude`,
   `ExtrudeCut` + **P1.4** `Fillet`, `Hole` (covers ~70 % of real parts)
3. **P2** `InsertComponent` + `AddMate` (Concentric, Coincident,
   Distance)
4. **P3.1** creation skills + **P3.3** aggregate `InspectDrawing` check
   wrapping missing-dims + title-block + tolerance-sanity. This is the
   headline QA feature.
5. **P4** `GetFeatureTree` + `Screenshot` (lets a vision model
   self-correct)

After wave 1 lands, the README demo changes from *“ask it to make a
circle”* to *“ask it to model and dimension a flanged bushing, then
check the drawing.”*

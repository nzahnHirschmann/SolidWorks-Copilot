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
- [ ] `MirrorEntities(axis)`
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
- [ ] `BoundarySurface(...)` (advanced shapes)

### 2.4 Modify features

- [x] `Fillet(edges[], radius, type)`, `Chamfer(edges[], distance, angle)`
- [x] `Shell(thickness, outward)` (selection-based face removal)
- [x] `Draft(angleDegrees, flip)` (neutral plane + faces pre-selected)
- [x] `SimpleHole(diameter, depth, throughAll)` — basic hole
- [ ] `HoleWizard(spec, points[])` (HoleWizard5 is wired in the API but
  the spec-driven wrapper is still pending)
- [ ] `Thread(face, spec)`
- [x] `LinearPattern`, `CircularPattern`, `MirrorFeature`

### 2.5 Body / surfacing

- [ ] `CombineBodies(op, bodies[])` (Add / Subtract / Common) — needs
  Body2 enumeration helper
- [x] `MoveCopyBody(...)`, `DeleteBody(...)`
- [ ] `SplitBody(...)`
- [x] `ThickenSurface(thickness, direction)`
- [ ] `KnitSurfaces(...)`, `TrimSurface(...)`

### 2.6 Configurations & equations

- [x] `AddConfiguration(name, comment?)`, `ActivateConfiguration(name)`,
  `ListConfigurations()`
- [x] `AddEquation("\"D1@Sketch1\" = \"D2@Sketch2\" * 2")` — global
  variables fall out of this naturally
- [ ] Dedicated `AddGlobalVariable(name, expression)` wrapper

### 2.7 Materials & mass

- [x] `SetMaterial(name)`
- [x] `GetMassProperties()` → mass, COG, volume, surface area, principal
  axes

---

## 3. P2 — Assembly modelling

- [x] `InsertComponent(path, x, y, z, configurationName?)` /
  `InsertComponentFromOpenDoc`
- [x] `FixSelectedComponent`, `FloatSelectedComponent` (selection-based)
- [ ] `MoveComponent`, `DeleteComponent`
- [x] **Mates** — `AddMate(type, distance?, angle?, alignment?, flip?)`:
  Coincident, Concentric, Parallel, Perpendicular, Tangent, Distance,
  Angle, Lock, Symmetric, Width (pre-select two refs)
- [ ] Remaining mate types: Cam, Gear, Slot, Path, Hinge, Screw
- [ ] `ReplaceComponent(old, newPath)`
- [x] `SuppressSelectedComponent`, `ResolveSelectedComponent`
- [x] `ListComponents(topLevelOnly)`
- [ ] `ListMates()`, `GetBoM()` — flat & indented
- [ ] `ExplodedView(steps[])`
- [x] **Interference detection**: `RunInterferenceDetection()` →
  component pairs + volumes. This is half of *“check accuracy”* in
  assemblies.
- [ ] `MeasureClearance(comp1, comp2)`
- [x] `ForceRebuild`
- [ ] `EvaluateMateErrors()`

---

## 4. P3 — Drawing creation & **accuracy checks**

### 4.1 Creation

- [ ] `CreateDrawingFromPart(part, template, sheetSize)` — actually
  populate the existing empty `CreateDrawing()`!
- [ ] `InsertView(part, viewType, x, y, scale)` — Front / Top / Right /
  Iso / Section / Detail / Broken / Auxiliary
- [ ] `InsertProjectedView(parentView, direction)`
- [ ] `InsertSectionView(parentView, line)`,
  `InsertDetailView(parentView, center, radius)`
- [ ] `InsertBomTable(view, type)`, `InsertHoleTable`,
  `InsertRevisionTable`, `InsertWeldmentCutList`
- [ ] `InsertCenterMarks(view)`, `InsertCenterlines(view)`
- [ ] `AutoBalloons(view, style)`

### 4.2 Annotations & GD&T

- [ ] `AddDimension(edge|distance, value?)` — driven + driving
- [ ] `AddDatumFeature(face, letter)`
- [ ] `AddGeometricTolerance(face, frame)` — flatness, perpendicularity,
  position, runout, etc.
- [ ] `AddSurfaceFinish(face, spec)`
- [ ] `AddWeldSymbol(...)`, `AddNote(text, x, y)`

### 4.3 **Accuracy / QA checks** (the unique value-add)

Implement as discrete read-only skills, then aggregate behind one
`[KernelFunction] InspectDrawing()` that returns a structured report.

- [ ] **Missing dimensions** — walk every visible edge/feature per view;
  flag any without a driving dimension and no implied symmetry.
  `CheckMissingDimensions(view)` → `[{ view, edgeId, length,
  suggestedDim }]`.
- [ ] **Feature-tree errors** — `ListFeatureTreeErrors()` returning every
  red/yellow `IFeature` with the underlying error string.
- [ ] **GD&T completeness** — every datum referenced in a control frame
  must exist; every critical face on the part should carry at least one
  tolerance. `CheckGdtConsistency()`.
- [ ] **Title block completeness** — `CheckTitleBlock(required: [
  PartNumber, Material, Finish, Revision, Drawn, Date ])` reading custom
  properties + sheet format text.
- [ ] **Tolerance sanity** — dimensions with no tolerance, or with
  tolerance ranges that violate ISO 2768-fine, are flagged.
- [ ] **Scale & paper** — section views whose scale differs from the
  parent without a note; views off the sheet boundary; overlapping
  views.
- [ ] **BoM vs assembly diff** — compare drawn BoM rows vs
  `ListComponents()` of the linked assembly. Flag extra/missing items,
  qty mismatch.
- [ ] **Reference checks** — broken view references (model moved/renamed),
  missing model edges (dangling annotations).
- [ ] **Standards compliance** — wrap SolidWorks Design Checker
  (`swDesignChecker`) and surface the results in chat.
- [ ] **Spell check** — every annotation through
  `IModelDocExtension.SpellCheckDocument`.

---

## 5. P4 — Querying & introspection

The model can't intelligently extend an existing part without being able
to *read* it.

- [ ] `GetFeatureTree(maxDepth)` — JSON of feature names, types,
  suppression state, parents
- [ ] `GetSelectedEntity()` →
  `{ type, name, area?, length?, faceNormal?, edgeEndpoints? }`
- [ ] `GetSketchEntities(sketchName)` — segments + dimensions +
  constraints
- [ ] `GetReferenceGeometry()` — planes, axes, origins
- [ ] `GetCustomProperties()` / `SetCustomProperty(name, value, type)`
- [ ] `MeasureDistance(e1, e2)`, `MeasureAngle(e1, e2)`,
  `MeasureMinRadius(face)`
- [ ] `GetBoundingBox()` — axis-aligned + body-aligned
- [ ] `Screenshot(view, width, height)` → base64 PNG. Lets a
  vision-capable model literally *see* the part. Biggest single
  accuracy multiplier once a vision model is selected.

---

## 6. P5 — Multi-step authoring (LLM-native UX)

Not new SW APIs — agent-loop features.

- [ ] **Plan preview & confirm** — show the proposed sequence of skill
  calls before executing; user clicks *Apply* or *Edit*.
  `ActionAnswerMessage` already half-exists
  ([SwPlanModel.cs](../Copilot.Sw/Models/SwPlanModel.cs)); wire it back
  into the streaming loop so the model can output a structured plan
  *instead of* (or in addition to) auto-calling.
- [ ] **Per-step status + rollback** — surface which feature call
  succeeded/failed; offer *Undo this step*.
- [ ] **Dry-run mode** — run skills against a cloned doc
  (`ISldWorks.OpenDoc6` with `swOpenDocOptions_Silent` + temp copy);
  show before/after screenshots.
- [ ] **Reference attachments** — drag a sketch image / PDF / DXF into
  chat; pass it to a vision model; have it produce a sketch plan.
- [ ] **Templates / macros library** — store named procedures (*“flange
  with M6 bolt circle, 6 holes, 80 PCD”*) that the model can invoke as
  one tool call.
- [ ] **@-mentions in chat** — `@Sketch3`,
  `@Feature/Boss-Extrude2`, `@Component<2>` resolve to selection at
  send time. Mirrors VS Code's `#file` UX.
- [ ] **Slash commands** — `/inspect-drawing`, `/check-mates`,
  `/mass-props`, `/new-part`. Reuse the existing skills toggle UI.

---

## 7. P6 — Productionisation

- [ ] **Skill telemetry** — log every kernel function call (name, args,
  duration, success). Without this you can't tell why a plan failed.
- [ ] **Permissioned tools** — mark skills as `RequiresConfirmation =
  true` (e.g., `DeleteFeature`, `CloseAll`); chat pane shows a Yes/No
  prompt.
- [ ] **Error normalisation** — `SolidWorksSkillException(code, message,
  recoverable)` so raw COM HRESULTs never reach the model.
- [ ] **Unit tests via mock `ISldWorks`** — `StandardAloneSw` test
  harness already exists; extend per new skill.

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

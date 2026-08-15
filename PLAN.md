# Hotkey AI — Assessment & Implementation Plan

Product name: **Hotkey AI**. Identifiers: process `HotkeyAI.Agent`, assemblies
`HotkeyAI.*`, storage `%LOCALAPPDATA%\HotkeyAI\`.

## Context

`Concept.txt` describes a Windows desktop app where a user registers a global hotkey,
describes an automation in natural language, and an LLM produces a structured plan that the
application executes, verifies, and repairs on failure.

This plan covers V1 only, under four decisions taken up front:

| Decision | Choice |
|---|---|
| V1 scope | **Deterministic core only** — no UI Automation, no mouse coordinates |
| Stack | **C# / .NET 10 (LTS) + WPF**, split into a tray engine process and a UI process |
| V1 planner | **None in-app.** Author DSL externally with Claude Code; app validates and executes |
| V2 planner | **API mode** — frontier model with JSON-schema structured output |
| DSL contract | **Schema-first.** Hand-authored JSON Schema is the source of truth; C# and docs conform to it |

Intended outcome for V1: a tray-resident app where you can author, test, and rely on ~5–10
personal automations bound to global hotkeys, reading the plan before anything executes,
with version history and rollback when something breaks.

**Who this is for, and what follows from it.** The user is someone who wants a hotkey to do
something and does not know AutoHotkey — and should not have to learn it, or learn where the
Startup folder is. That is the entire reason the product exists. It follows that Hotkey AI is
**the only global hotkey manager on the machine**: a user running AutoHotkey scripts is by
definition not the target user, so two managers is a configuration to resolve rather than a mode
to support, and where they collide the other scripts go. This is a product decision and it lifts
a real design burden off the agent — see `docs/spike-findings.md` § A2.

The consequence worth naming: **V1 has no natural-language input surface at all.** The app
is an engine plus an authoring bridge. That's the right trade — it removes the project's
highest-risk item (plan quality from a constrained model) and defers it until the DSL has
stopped moving, and for a personal tool you'll author ~10 automations and press the hotkeys
thousands of times. The authoring surface is the least-used part of the app by roughly 100:1.

---

## Assessment

### What's genuinely strong in the concept

**1. LLM plans, application executes.** This is the load-bearing idea and the concept is
right to call it "extremely important." It converts an open-ended code-generation problem
into a constrained-output problem. Everything good downstream follows: you can validate
before executing, show the user a readable plan, diff versions, and roll back. Generating
AHK or PowerShell instead would make all four impossible.

It's also what makes V1's external-authoring shortcut possible at all — because the planner
is a pure function from text to a validated artefact, *who or what runs it is replaceable*.
A human with Claude Code is a valid implementation of that interface.

**2. The execution hierarchy.** Native API → app CLI → UI Automation → synthetic input, in
that order, is the single most valuable engineering constraint in the document. The concept's
own examples prove it: `launch_process("Code.exe", path)` beats twelve UI steps, and Spotify
play/pause is a `WM_APPCOMMAND` message — not a UIA click. Two of the doc's three flagship
examples need no UIA at all. That's why V1 can defer it.

**3. AI is out of the hot path.** Generation happens once per automation; execution is pure
engine. Make this an explicit architectural invariant. It's what makes V1 viable with no
planner and V2 cheap with an API one — latency and cost never scale with usage.

**4. Failure → repair as the differentiating loop.** Feeding the original request, the plan,
the timestamped execution log, and the user's own description of what went wrong back into
the planner is a strong product idea. In V1 the app assembles that bundle and you paste it
into Claude Code; in V2 the same bundle goes to the API. The app owns the hard parts either
way — log capture, failure classification, diffing, rollback.

**5. Local-first storage with versioning.** JSON automations plus SQLite for history means
no cloud dependency for the thing you actually rely on, and `[Restore Version 2]` is a real
feature that competitors (AutoHotkey scripts in a folder) don't have.

### What will actually hurt

Ranked by how much time each will cost you.

**1. The authoring handoff is now the product's weakest seam.** With no in-app planner, the
loop is: describe intent in a terminal → get JSON → get it into the app → find out it's
subtly wrong → repeat. If that shuttling is manual, friction decides whether you ever create
automation #4. Three things fix it, and they are V1 requirements, not conveniences:

- **Publish the contract to disk.** `HotkeyAI.Core` exports `docs/dsl.schema.json` plus a
  generated `docs/capabilities.md` (primitive list, parameters, verifiable postconditions).
  A `CLAUDE.md` points at both. Claude Code then reads the actual contract rather than
  guessing from prose.
- **Expose a `validate` verb.** `HotkeyAI.Cli validate <file>` returns structured errors
  with JSON paths and exit codes. This is the highest-leverage item in V1: it turns an
  external AI into a *self-correcting* planner — generate, validate, read errors, fix, repeat
  — with no human in the middle of the correction loop.
- **Watch the automations folder.** Drop a `.json` in and the agent picks it up live,
  validates, and toasts success or the first error. No import dialog in the way.

Get these right and external authoring is genuinely pleasant. Get them wrong and V1 is a
JSON editor with a hotkey.

**2. The security model and the authoring workflow are in direct conflict.** Worth flagging
explicitly because it's easy to miss until it bites. Signing automation files and refusing
tampered ones (control 4 below) is exactly right against local malware — but under external
authoring, *every legitimately authored file is unsigned*. Enforce the control naively and
your own workflow is indistinguishable from an attack.

The resolution is trust-on-first-use, not relaxation: an unsigned or changed file is not
refused, it is **loaded disabled and marked unverified**, and the UI prompts *"New automation
'X' detected — review and enable?"* rendering the human-readable plan. On approval the app
HMACs it. Malware still can't get silent code execution, because nothing unsigned ever runs
without a human reading the plan first. Design this into Goal 1; retrofitting it means
choosing between a broken workflow and a hole.

**3. Global hotkey registration on Windows is hostile.** `RegisterHotKey` is
first-come-first-served process-wide, and when it fails it tells you *nothing* about who
holds the combination. So the concept's "⚠ Unable to register — try Ctrl+Shift+P" is
achievable, but "Ctrl+Alt+P is held by Foobar.exe" is not. Also: `Win`-key combinations
reserved by the shell can't be captured, and Ctrl+Alt+Del never can.

**4. UIPI / UAC is a hard wall, not a bug.** An unelevated process cannot send input to an
elevated window. If VS Code, a terminal, or Task Manager runs as admin, `send_keys` into it
silently does nothing — no error, no exception. Detect the integrity-level mismatch and
report it as a first-class failure reason rather than letting the automation appear to
succeed. Running elevated "fixes" this at the cost of a bad security posture and breaking
Startup-folder autostart. For V1: stay unelevated, detect, report.

**5. "Did it work?" is undecidable in general.** The Observer box in the concept hides this.
Be honest in the data model: only a closed set of postconditions is machine-checkable
(process running, window matching title/class exists, path exists, clipboard content,
foreground process). Every action either carries a verifiable `expect` or is explicitly
tagged `unverified` in the plan and rendered as such. Do not let the UI imply verification
that didn't happen.

**6. The concept has no security model.** `launch_process` with arbitrary path and arguments
is arbitrary code execution on a keypress. `type_text` into a focused password field is
credential theft. And a program that registers global hotkeys, synthesises input, and spawns
processes is textbook keylogger behaviour to Defender and any EDR — expect to be flagged.
None of this is mentioned in the concept; controls are listed below.

**7. There is no kill switch.** An automation that steals focus in a loop or repeats
keystrokes needs a panic abort. Safety feature, not a nice-to-have.

**8. `select_item` hides a whole application.** The example DSL's `select_item` implies a
fuzzy-search picker overlay with keyboard navigation, always-on-top behaviour, and correct
focus restore. That's a real component, not a primitive. Budget for it.

**9. Windows fragmentation.** DPI awareness, multi-monitor coordinate maths, and Win10 vs
Win11 window-management differences will all bite. V1 avoids the worst by having no mouse
primitives.

---

## Planner strategy: V1 external, V2 API

### Why not local — the arithmetic

The original instinct was local-only to avoid API costs. Worth recording why that premise
doesn't hold, since it's the concept's own architecture that undermines it: **generation
happens once per automation; execution costs nothing.** This is not a metered per-use cost.

Assuming a ~4K-token system prompt (capability list + schema + few-shot plans), a ~400-token
plan, and two repair round-trips at ~6K input each — ~16K input and ~1.2K output per
automation authored:

| Model | Per automation | 10 automations, lifetime |
|---|---|---|
| Claude Opus 5 ($5 / $25 per MTok) | ~$0.11 | **~$1.10** |
| Claude Sonnet 5 ($3 / $15) | ~$0.07 | ~$0.70 |
| Claude Haiku 4.5 ($1 / $5) | ~$0.02 | ~$0.20 |

With prompt caching — the schema and capability list are a stable prefix, cache reads bill at
~0.1× — the input side drops ~90% within an authoring session, taking Opus 5 to roughly $0.05
per automation. These are estimates from an assumed prompt size; verify with `count_tokens`
once the real system prompt exists. Running a local model to save ~$1 while accepting worse
plans on multi-step requests is a bad trade; V2 goes to the API.

### V1: the authoring bridge

No `IPlanner` implementation ships in V1. Instead the app exposes the three surfaces that let
an external AI act as the planner:

```
       you + Claude Code                    Hotkey AI
              │                                 │
   reads  docs/dsl.schema.json  ◄───── generated from Core
          docs/capabilities.md   ◄───── generated from the primitive registry
              │
   writes automations/*.json     ─────► FileSystemWatcher → validate → load disabled
              │                                 │
   runs   HotkeyAI.Cli validate   ◄────► structured errors, JSON paths, exit codes
              │                                 │
              └─ self-corrects ──┘         you review plan → enable → HMAC
```

`HotkeyAI.Cli` verbs, all thin wrappers over Core and the agent pipe:

| Verb | Purpose |
|---|---|
| `validate <file>` | Schema + policy errors as structured JSON. The self-correction loop. |
| `explain <file>` | Render the human-readable step list — lets you check intent, not just syntax |
| `import <file>` | Explicit import with version bump, for when you'd rather not use the watcher |
| `run <name> --test` | Trigger a test run and stream the execution log to stdout |
| `logs <name>` | Last run's log, for pasting into a repair prompt |

### V2: swapping in the API

`IPlanner` lands in V2 as a single implementation, and V1's artefacts are already its inputs:
the exported JSON Schema becomes the structured-output schema; `docs/capabilities.md` becomes
the system prompt's capability list; the repair-context bundle becomes the repair request. The
seam is designed in V1 rather than discovered in V2.

Note: a Claude Code or Claude subscription is not a general-purpose API credential for a
third-party app. V1 means *you* use an AI tool to author. V2 means Hotkey AI calls an API with
your own key.

---

## Architecture

```
┌─────────────────────────────────────────────┐
│  HotkeyAI.Ui (WPF, launched on demand)      │
│  automation list · editor · plan preview    │
└───────────────────┬─────────────────────────┘
                    │ named pipe (JSON-RPC)
┌───────────────────▼─────────────────────────┐   ┌──────────────────┐
│  HotkeyAI.Agent  (tray, always resident)    │◄──│  HotkeyAI.Cli    │
│  ├─ HotkeyHost        RegisterHotKey pump   │   │  validate/explain│
│  ├─ AuthoringBridge   watcher + TOFU gate   │   │  import/run/logs │
│  ├─ Validator         schema + policy       │   └──────────────────┘
│  ├─ Executor          primitives + observer │
│  ├─ RepairContext     bundle exporter       │
│  └─ Store             JSON + SQLite         │
└─────────────────────────────────────────────┘
              (V2 adds: IPlanner → CloudPlanner)
```

Two processes, deliberately:

- A frozen or crashed UI must never block a hotkey. The engine owns the message pump.
- It keeps the trust boundary clean and leaves the door open to running the engine at a
  different integrity level later.

**Amended once the picker was built.** The overlays — picker, input, confirm, toast — live in
`HotkeyAI.Ui` but are hosted *in the agent's process*, on a dedicated STA thread, not behind the
pipe. They sit on the execution path: the engine awaits a choice, so routing every `show_picker`
through a process launch and an IPC round trip would add startup latency and a new hang mode to
the flagship example, in exchange for nothing the user can see. The hotkey pump is unaffected
either way, because the agent already runs executions off its main thread.

The rationale above still holds for the *management shell* — the automation list, editor and plan
preview — which is genuinely launched on demand and is where a frozen UI could otherwise matter.
The price of hosting the overlays in-process is that an exception on the UI thread would take the
hotkey owner down with it, so `WpfPrompts` catches and degrades to "cancelled" rather than letting
one fail. That is not theoretical: the first run of the picker crashed the process outright.
- Startup is the agent only; the UI is launched from the tray.

`HotkeyAI.Core` holds the DSL types, schema, validator, and doc generators with no Windows
dependencies — unit-testable without a desktop session, which matters for the regression
suite.

---

## The DSL

### Schema-first contract

`schema/hotkeyai-dsl-v1.schema.json` — hand-authored, JSON Schema draft 2020-12 — is the
single source of truth. It is also the published artefact: Claude Code reads it in V1, and V2
hands the same file to structured outputs verbatim. Nothing generates it.

Two things flow *from* it:

- **`docs/capabilities.md`** — generated by walking the schema and emitting the primitive
  list, parameters, and postconditions as Markdown. This makes the schema's `description`
  fields load-bearing product content, not comments: they are the prompt material the planner
  reads. Write them for a model that has never seen this DSL.
- **C# types** — conformance-enforced rather than generated. See below.

**Don't chase full codegen.** For a 20-primitive DSL, every C# generator's weakest area is
exactly where this DSL is most interesting: `oneOf` with a `const` discriminator, which the
`actions` array is built entirely from. Hand-write the records using .NET's native polymorphic
serialisation, which maps cleanly onto that shape:

```csharp
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LaunchProcessAction), "launch_process")]
[JsonDerivedType(typeof(ShowPickerAction),     "show_picker")]
// ...
public abstract record HotkeyAction;
```

Then enforce schema authority with a **conformance test** in `HotkeyAI.Core.Tests` asserting
both directions: every `JsonDerivedType` discriminator appears in the schema's `oneOf`, and
every schema action type has a matching record with matching required properties. ~100 lines
of hand-written records plus one test beats fighting a generator, and the schema still wins
every disagreement because the test fails.

Runtime validation uses `JsonSchema.Net` (json-everything) — full draft 2020-12 support, which
matters because the discriminated-union and `$ref` handling has to be correct.

### Two validation layers, and where each constraint lives

Keep the schema inside the **structured-output-expressible subset** so V2 can hand it to the
API unchanged. Everything else is a policy check in Core. This is not a limitation to work
around — it's the boundary that decides what the planner can be *constrained* to versus what
it merely gets *told*.

| Constraint | Layer | Why |
|---|---|---|
| Known action type, discriminator, required params | **Schema** | Expressible; the sampler enforces it |
| `enum` values, string `pattern`, `format` | **Schema** | Expressible |
| `additionalProperties: false` everywhere | **Schema** | Required by structured outputs; also our allowlist rule |
| `timeoutMs` within 100–300000 | **Policy** | Numeric constraints (`minimum`/`maximum`) are not in the supported subset |
| Path under an allowed root; `resolve` names a known app | **Policy** | Needs runtime config and the app registry |
| Total action count ≤ 200; nesting depth | **Policy** | Not expressible as a schema constraint |
| Variable used before assignment; type mismatch on `${…}` | **Policy** | Cross-field dataflow |

### ⚠ The DSL as sketched is recursive — and structured outputs reject recursive schemas

`if` and `foreach` contain nested action arrays, so `Action → if → Action` is a `$ref` cycle.
Recursive schemas are **not supported** by structured outputs, so this would have surfaced as a
hard blocker partway into V2, after the schema was baked into every stored automation.

Fix it now, and note it costs nothing we wanted: **three action levels**, expressed as explicit
non-cyclic levels. Stated precisely, because "depth 3" is ambiguous and the ambiguity already
caused one wrong test: a control-flow action may contain another control-flow action, and that
inner one may contain **leaf actions only**. So at most *two* nested `if`/`foreach`, with plain
actions at the third level.

```jsonc
"$defs": {
  "LeafAction":  { "oneOf": [ /* the ~18 non-control primitives, shared by $ref */ ] },
  "ActionL2":    { "oneOf": [ { "$ref": "#/$defs/LeafAction" } ] },
  "ActionL1":    { "oneOf": [ { "$ref": "#/$defs/LeafAction" },
                              { "$ref": "#/$defs/IfL1" }, { "$ref": "#/$defs/ForEachL1" } ] },
  "ActionL0":    { "oneOf": [ { "$ref": "#/$defs/LeafAction" },
                              { "$ref": "#/$defs/IfL0" }, { "$ref": "#/$defs/ForEachL0" } ] }
  // IfL0.then → ActionL1[] ; IfL1.then → ActionL2[] ; L2 has no control flow
}
```

`$ref` to shared leaf definitions is fine — only *cycles* are the problem. The cost is six
small wrapper definitions (`IfL0/L1`, `ForEachL0/L1`, and their bodies), roughly 60 lines of
ceremony in the schema file.

This is also strictly *more* aligned with the design goal already stated below —
"Turing-incomplete on purpose → statically analysable." Unbounded nesting was never wanted;
now the contract says so, and the same file works for both V1 authoring and V2 structured
outputs. Real automations do not nest three deep.

### Improvements over the concept's sketch

Each one and why it's needed:

| Change | Reason |
|---|---|
| `schemaVersion` on every document | You will change the DSL; old automations must still load |
| Per-action `id` | Log lines, failure reports, and repair prompts need to reference a specific step |
| Per-action `expect` (postcondition) | Makes the Observer part of the plan rather than a mystery box |
| Per-action `timeoutMs`, `onError` | An automation must never hang forever or silently continue past a failure |
| `argv` as an **array**, never a command string | Removes shell-injection as a category. No `cmd /c`, no `powershell -Command` primitive in V1 |
| Typed `variables` with declared slots | `${selected_item}` needs a type so the validator can reject a path where a window title is expected |
| `if` with a restricted predicate grammar; `foreach` bounded over a materialised list; **nesting capped at depth 3** | Turing-incomplete on purpose → statically analysable, no unbounded loops, no runaway automations — and the depth cap is what keeps the schema non-recursive for structured outputs |

Example — the concept's project launcher, expressed properly:

```json
{
  "schemaVersion": 1,
  "id": "a3f1...",
  "name": "Project Launcher",
  "trigger": { "type": "hotkey", "keys": ["CTRL", "ALT", "P"] },
  "variables": [
    { "name": "selected", "type": "path" }
  ],
  "actions": [
    { "id": "s1", "type": "list_directories",
      "path": "C:\\Users\\vaibhav\\Desktop\\Projects", "depth": 1,
      "into": "candidates" },
    { "id": "s2", "type": "show_picker",
      "source": "${candidates}", "prompt": "Open which project?",
      "into": "selected", "timeoutMs": 30000, "onError": "abort" },
    { "id": "s3", "type": "launch_process",
      "resolve": "vscode", "argv": ["${selected}"],
      "expect": { "type": "window_exists", "processName": "Code",
                  "titleContains": "${selected.name}", "withinMs": 15000 } }
  ]
}
```

`resolve: "vscode"` matters: the plan names a *logical* application and the engine resolves it
against a local app registry (known install paths, `App Paths` registry key, PATH). This is
precisely the fix the concept's repair example lands on — and doing it by design means the
"it opened Chrome instead of VS Code" failure can't happen in the first place.

### V1 primitive set

Deliberately small; grow it only when a real request needs it. Adding a primitive means:
schema first (with a written `description`), then the record, then regenerate
`capabilities.md`. Skip the description and the planner has no idea what the primitive is for.

- **Process** — `launch_process`, `terminate_process`, `wait_for_process`
- **Window** — `focus_window`, `minimize_window`, `maximize_window`, `move_window`,
  `close_window`, `wait_for_window`
- **Input** — `send_keys`, `type_text`, `send_appcommand` (media keys — the correct Spotify
  path, no UIA)
- **Files** — `list_directories`, `list_files`, `path_exists`, `open_path`
- **Clipboard** — `set_clipboard`, `get_clipboard`
- **Own UI** — `show_picker`, `show_input`, `notify`
- **Control** — `wait`, `if`, `foreach`, `abort`

Verifiable postconditions: `process_running`, `window_exists`, `path_exists`,
`clipboard_matches`, `foreground_process_is`. Anything else → the action is tagged
`unverified` and the plan preview says so.

---

## Non-negotiable safety controls

V1 requirements, not polish. Each prevents a specific failure I can name.

1. **Panic abort.** A dedicated always-registered hotkey (default `Ctrl+Alt+Shift+Esc`) that
   kills the running automation and releases all held modifier keys. Plus a global cap of 200
   actions and 120 seconds per run, enforced by the executor.
2. **Validator is an allowlist, not a blocklist.** Unknown action `type` → reject. Unknown
   field → reject. `launch_process` must use `resolve` against the app registry or an absolute
   path under a configured allowed root. No shell primitive exists to abuse.
3. **Sensitive-window guard.** Before any `send_keys` / `type_text`, check the foreground
   window: refuse if it is a UAC consent dialog, a credential prompt, or a control with the
   password style. Detect and report integrity mismatch rather than failing silently.
4. **Trust-on-first-use for automation files.** Store under `%LOCALAPPDATA%` with a per-user
   ACL and HMAC each file with a key in DPAPI. An unsigned or changed file is **loaded
   disabled and marked unverified**, never refused outright, and never run until the user has
   seen the rendered plan and approved it — at which point it's signed. This is the resolution
   of the conflict in challenge 2: it keeps malware from getting silent code execution while
   making hand-authored files a first-class input.
5. **Confirmation on destructive actions.** `terminate_process` and `close_process` prompt on
   first use per automation, remembered thereafter.
6. **Never log secrets.** Execution logs record action ids and outcomes, and redact
   `type_text` payloads and clipboard content by default. This matters more in V1 than V2 —
   you will be pasting logs into a repair prompt by hand.
7. **No egress in V1.** Nothing leaves the machine; the authoring bridge is filesystem and a
   local pipe. Before V2's API mode ships, add path/title redaction at the boundary — prompts
   and repair bundles will otherwise carry file paths and window titles.

If this is ever shared with colleagues at Solulever, re-open items 4, 6 and 7 plus code
signing and a Scrut.io control mapping before distribution — window titles and file paths in
prompts and logs are PII/confidential-adjacent under SOC2/ISO 27001 obligations.

---

## Phased plan

ProtrackLite hierarchy. Estimates assume one developer working focused sessions; sequencing
guidance, not commitments. Total ≈8 weeks, down from ~10 now that the planner is out of V1.

### 🎯 Goal 0 — De-risk the unknowns — **DONE**, see `docs/spike-findings.md`

Run late, after the engine rather than before it. Three findings changed the work:

- **A bare-key hotkey registers successfully.** Windows will let an app swallow `P`
  system-wide, so the policy rule requiring a modifier is load-bearing rather than tidiness.
- **`CTRL+SHIFT+W` cannot register on this machine**, and that was `work-environment.json`'s
  trigger — a first-run example whose hotkey fails is the first thing a new user meets. Changed
  to `CTRL+ALT+W`; a sweep script now checks every shipped example.
- **Elevated-window detection works via the denial**, not just the token: `OpenProcess`
  returning `ERROR_ACCESS_DENIED` is itself proof of higher integrity. Both paths verified.
- **`processName: "explorer"` matches the desktop shell**, and `className` is worthless for
  Chromium apps (Chrome, Cursor and Teams all report `Chrome_WidgetWin_1`). Fed back into the
  schema descriptions. **The agent's window finder must exclude `Progman` and `WorkerW`**, or
  automations will act on the desktop.

Still unverified: that `SendInput` into an elevated window fails *silently*. Detection is
proven, the silence is not — no elevated GUI window was running and creating one needs a UAC
prompt a person must click. Harmless, since the engine refuses before sending, but it should not
be written up as observed.

<details>
<summary>Original plan for this goal</summary>

### 🎯 Goal 0 — De-risk the unknowns (≈3 days)

Three assumptions, three throwaway spikes.

- ✅ **Hotkey spike** — register/unregister at runtime, observe the failure mode for a taken
  combination, confirm which reserved combos are unreachable.
- ✅ **Input spike** — `SendInput` into a normal app, then into an elevated app. Confirm the
  silent-failure behaviour and find a reliable detection method.
- ✅ **Verification spike** — confirm `window_exists` with a title match is reliable for VS
  Code, Explorer, and a browser.

📌 Deliverable: a one-page findings note. Any spike that fails changes the plan.

</details>

### 🎯 Goal 1 — Engine + authoring bridge (≈3 weeks)

The bulk of V1. With no planner, this *is* the product.

- ✅ **DONE — `schema/hotkeyai-dsl-v1.schema.json`.** 25 action discriminators, three
  bounded action levels, `additionalProperties: false` throughout, a written `description` on
  every property. Verified: valid draft 2020-12, no `$ref` cycles, no keywords outside the
  structured-output subset.
- ✅ **DONE — `tools/schema-checks/`.** Three Python checks standing in for the C# tests until
  the SDK is available, and staying afterwards because they cover the schema *as a document*:
  `check_schema.py` (hygiene, cycles, subset compliance, description coverage),
  `validate_examples.py` (examples validate; 14 negative cases are rejected),
  `gen_capabilities.py` (generates `docs/capabilities.md`, `--check` mode for CI).
- ✅ **DONE — `examples/`** — eight reference automations at **100% action-type coverage**,
  enforced as a CI gate: adding a primitive without an example that exercises it now fails,
  because an unexercised primitive is a shape nobody has validated. They double as the first
  regression-corpus entries and the first-run examples.
- ✅ **DONE — `.github/workflows/schema.yml`** — the three checks in CI.
- ✅ **DONE — repo scaffolding** — `CLAUDE.md` (the authoring contract for the V1 loop),
  `README.md`, `.gitignore`, `.gitattributes`, `git init`.
- ✅ `HotkeyAI.Core` — hand-written records with `[JsonPolymorphic]`, the conformance test,
  `JsonSchema.Net` validation, the policy-check layer, app registry, variable resolution.
  **No doc generator here** — `tools/schema-checks/gen_capabilities.py` owns that, so there is
  only ever one implementation and nothing to keep byte-identical.
- ✅ `HotkeyAI.Agent` — tray host, hotkey pump, executor with the full V1 primitive set,
  observer/postcondition checker, structured execution log.
- ✅ `AuthoringBridge` — `FileSystemWatcher` on the automations folder, validate-on-change, and
  the TOFU gate from control 4. The watcher surfaces an approval; it can never skip one.
- ✅ `HotkeyAI.Cli` — the five verbs. Build `validate` and `explain` first; they're what make
  external authoring work.
- ✅ Safety controls 1–7, wired in from the start rather than retrofitted.
- ⬜ Store — automation JSON is done, with approvals under DPAPI and the switched-off list beside
  it. No SQLite: nothing yet needs versions or run history, and a database before there is a
  question it answers is a liability. Revisit with Goal 3's version history.
- ✅ Autostart, but **not** via Task Scheduler. `schtasks /Create /SC ONLOGON` is refused with
  "Access is denied" for a non-elevated user on a default Windows 11 install, and elevation was
  not an acceptable price for a hotkey tool. It is a per-user Run entry, which needs no rights,
  is trivially reversible, and shows up in Task Manager's Startup tab — where software that reads
  every keystroke ought to be visible. The delay the task was wanted for is moot: Run entries are
  processed after the shell is up.
- ✅ `CLAUDE.md` in the repo pointing at the generated schema and capability docs, so the
  authoring loop works from a cold start in a new session.

**Exit criterion:** with Claude Code and nothing else, author the three reference automations
(project launcher, Spotify play/pause, work-environment setup) end to end — generate,
`validate`, fix, drop in folder, approve, run from a cold boot. Panic key stops all three
mid-flight.

### 🎯 Goal 2 — UI shell and picker (≈1.5 weeks)

- ✅ **Done.** `show_picker` overlay — fuzzy search, keyboard-only navigation, always-on-top,
  correct focus restore on cancel. Ranking is `FuzzyMatcher` in Core, unit tested; the overlay
  renders and decides nothing. `show_input`, the destructive-action confirm and the `notify` toast
  ship with it, since `IPrompts` is one interface and a half-implemented one would leave the agent
  reading a console that is not there.
- ✅ **Done.** WPF shell — automation list with enable/disable, and the
  TOFU approval prompt, which shows the rendered plan before the button that grants it. Opened
  from the tray by double-click or menu. Disabling is a separate, reversible flag rather than a
  revocation: making someone re-approve a plan they never changed just to switch it back on would
  turn the approval prompt into something to click past. Capturing a chord in the UI, with the
  live availability check, is still outstanding — chords come from the plan's JSON today.
- ✅ Plan preview — human-readable step list with `unverified` tags rendered honestly. Shared
  renderer with `cli explain`, so the two can't disagree.
- ✅ **Partly done.** The dashboard has a paste-and-check surface: describe an automation, copy a
  prompt for Claude Code, paste the JSON back, and check, preview and save it without leaving the
  window. That is the V1 planner made explicit — and the exact seam V2 replaces, since the prompt
  it builds is what the API call will send. A full editor with inline errors and snippets is
  still outstanding.

**Exit criterion:** an unsigned automation dropped in the folder surfaces an approval prompt
showing the correct rendered plan, and can be enabled, run, and disabled from the UI.

### Two kinds of verified, and why only one of them is the engine's

The engine can check *effects*: a postcondition says a process is running, a window exists, the
clipboard contains a string. Five things, and no more — an action without one is reported as
**unverified**, meaning it ran and the engine cannot confirm it had any effect.

What the engine can never check is *intent*. "Did this automation do what I meant?" is not a
property of the machine state, and no number of postconditions gets at it: a plan can pass every
check it carries and still open the wrong folder. Only the person who wrote it knows.

So there are two claims, and they are kept apart deliberately:

| | Who makes it | About what | Where it appears |
|---|---|---|---|
| **unverified** | the engine | one action, per run | the transcript, the plan preview |
| **not tested / works / not working** | the user | the whole automation | the dashboard, `hotkeyai list` |

The user's verdict is recorded against the plan's content hash, exactly as approval is, so editing
a plan drops it back to *not tested* — "I tested this" cannot outlive the thing that was tested.
It deliberately does **not** gate execution: you have to run an automation to find out whether it
still misbehaves, and this must never become another reason a hotkey quietly stops firing.

Marking one as *not working* opens the repair dialog immediately, because the moment someone
decides something is broken is the moment they can say what is wrong with it. What they type is
kept against the automation and pre-fills the box next time.

### 🎯 Goal 3 — Test, repair, regression suite (≈1.5 weeks)

- ✅ Test-run mode as a first-class feature — timestamped log exactly as the concept sketches,
  rendered live.
- ✅ **Done.** Repair exporter — the dashboard's Repair button bundles the plan, the execution
  transcript of the run being complained about, what the user says went wrong, and the same rules
  the authoring prompt carries, into one copyable block.

  The assembling is the whole value. Someone whose automation misbehaved knows what they expected
  and nothing else: they do not have the JSON to hand, cannot recall which step failed, and would
  never think to mention that three actions ran unverified — which is frequently the actual fault.
  The application already knows all of it.

  Two deliberate details. The transcript is *shown* in the dialog rather than only attached,
  because half the time it answers the question on its own. And the prompt explicitly permits the
  answer "the plan is not the problem" — an automation that failed because an application was not
  running needs no repair, and a prompt that only ever asks for a rewrite will get one.

  Repair appears only on automations that have actually run. The last run is held in memory, so a
  run from before the agent started is in the log file but not offered here: parsing a transcript
  back out of a text log to feed a repair prompt would be building on a guess.
- ✅ Plan diff view — old vs new side by side. The concept shows only the new plan; the diff is
  what makes an AI-authored change reviewable.
- ✅ Version history with `[Restore Version N]`, backed by the SQLite version table.
- ✅ **Done.** Regression suite — 57 golden plans in `tests/corpus`, held to four things: they
  validate, they round-trip through JSON, they render exactly as before, and they execute against
  `FakeDesktop`. Coverage is gated, not hoped for: every action type, postcondition, predicate and
  composite condition must appear, and the plan count must stay between 40 and 60.

  It earned its keep before it was finished. The first full run failed on `real-daily-standup`,
  because the app registry exists in two places — `AppRegistry` in Core, which the validator reads,
  and `AppResolver` in Windows, which resolves executables — and the previous change had added
  Teams, Discord and Cursor to the second only. Every plan naming `"app": "teams"` would have been
  refused by validation while resolving perfectly at run time. Fixed in the same change.

  The "execute in a VM snapshot" half is still outstanding: execution here is against the fake
  desktop, which proves dispatch and control flow but not that anything happens on a real screen.

**Exit criterion:** the suite runs from one command; a deliberately broken automation is
repaired via export → Claude Code → re-import, with the diff reviewed before enabling.

### 🎯 Goal 4 — Make it liveable (≈1.5 weeks)

- ✅ Settings, import/export, tray menu polish.
- ✅ Single-file self-contained publish; document that Defender/EDR will likely flag it.
- ✅ First-run experience: ship the three reference automations as editable examples, already
  signed.
- 📌 Deferred to V2, deliberately: `IPlanner` + API mode, UI Automation primitives, Inspect
  Mode with the element tree, mouse/coordinate actions.

---

## Known gaps at V1

Recorded at the `v1.0.0` tag, so none of this is discovered again by surprise. Everything here is
missing on purpose or unfinished for a stated reason — nothing is a mystery.

### ~~Hotkey capture in the UI~~ — done

The chord on each dashboard row is now the button that changes it. Pressing a combination checks
it live, and the check runs on every keypress rather than on save, so a taken chord is reported
while the user is still holding it.

Three things the implementation had to get right, one of which was not obvious:

- **Hotkeys are released while the capture window is open.** Windows delivers a registered chord
  to the thread that registered it, never to the focused window — so while the agent held
  `Ctrl+Alt+X`, pressing it ran that automation instead of reaching the capture box. The window
  was blind to precisely the combinations it exists to report on. Everything is unregistered for
  the life of the window, panic key included, and restored on close.
- **Our own conflicts are named.** `RegisterHotKey` can never say who holds a chord, but the store
  can: "already used by Close Distractions" rather than "unavailable". With hotkeys suspended this
  is the only source of truth about our own bindings, so it is load-bearing rather than a nicety.
- **Rebinding does not revoke approval.** Approval is granted against the file's content hash, and
  changing a trigger changes the file — correct when something changes underneath the user, wrong
  when the user just asked for it in this window and nothing the automation *does* has moved. The
  approval is re-granted only because this code made the edit and knows it touched nothing else.

Still honest about what it cannot know: a chord grabbed by a low-level keyboard hook reports as
available and then never fires, and the window says so in as many words. Rewriting the trigger
also reformats the plan's JSON, since it is written back through a parser rather than patched.

### ~~The reference automations have three defects on a real machine~~ — two fixed, one deliberate

Found by running all eight on the author's desktop.

1. **Fixed.** `open-solution.json` searched `*.sln` and missed `.slnx`, which is the current
   format — so on a machine whose only solution is this repository's own `HotkeyAI.slnx`, the
   example found nothing at all. The pattern is now `*.sln*`, which also picks up `.slnf`
   solution filters.
2. **Fixed.** Teams, Discord and Cursor are in the app registry. The current Teams is the Store
   build — process `ms-teams`, not the old `Teams.exe` — and both are listed, because the classic
   client is still on plenty of machines. `close-distractions.json` now asks Teams to close, but
   never force-kills it the way it does Slack: Teams is usually someone's work phone, and killing
   a call is not a distraction-blocking feature.
3. **Left alone, deliberately.** `work-environment.json` still names `vscode`, so it launches VS
   Code for someone who uses Cursor. The examples are simultaneously the shipped first-run set and
   the corpus that enforces 100% action coverage, and narrowing them to one person's installed
   editor would cost both roles. `cursor` now resolves, so anyone can name it in their own copy —
   changing which editor a *shipped* example assumes is a different decision.

   Doing this properly means a logical "editor" that resolves to whichever is installed, and that
   is a larger change than it looks: the plans also select windows by process name, so the
   registry would have to carry the process name alongside the executable, and every selector
   would need to read it. Worth doing, worth doing on purpose.

### `work-environment` rearranges windows you are using

Working as designed — the plan says it reuses anything already open, and "set up my workspace"
means arranging existing windows. It is still startling when it grabs a browser window mid-task.
Deciding between "arrange everything" and "arrange only what I launched" is a product question,
and the second option makes the automation useless on its second run of the day.

### ~~No folder watcher~~ — done

Dropping a plan into the automations folder is now noticed within a second, and the agent
reloads and rebinds itself.

The constraint it was scoped under is intact and worth restating, because it is the whole reason
this feature is safe: **the watcher cannot make anything run.** A file arriving is classified like
any other, so new or edited content is inert until a person has read the rendered plan and
approved it. What the watcher shortens is the distance between saving a file and being *asked*.
Verified by dropping one in: it appeared immediately as "new — review the plan and approve it
before it can run", and stayed there.

Two details that make it usable rather than irritating:

- **A settling period.** One save is several filesystem events, and a plan arriving over a sync
  client lands in pieces, so every event restarts a 750 ms timer and only the settled state is
  read. Reacting to the first event means parsing a half-written file and reporting it as invalid.
- **Content, not timestamps.** The folder is fingerprinted by file name and content hash, so
  saving a file without changing it is ignored, and the agent does not react to its own writes —
  rebinding a hotkey and saving a pasted plan both rewrite files here and would otherwise bounce
  straight back as external changes.

Editing an automation that was already approved drops it out of live immediately, which is
approval being bound to content rather than to a file name. Restoring the original bytes brings
the approval back on its own.

### `hotkeyai import` and `hotkeyai logs` are stubs

Both report that they are not implemented. `logs` is now largely served by the tray's *View log*,
and `import` was intended for pulling a plan from a file or the clipboard — which the dashboard's
paste box has partly overtaken.

### No planner in the application

By design for V1: the LLM plans, the application executes, and in V1 the LLM is a person with
Claude Code. The dashboard makes that explicit rather than pretending otherwise — it builds the
prompt and takes the JSON back.

V2 replaces the clipboard with an API call using the same schema for structured output, and needs
three decisions that are the user's, not the code's:

- **Where the API key lives.** DPAPI at user scope is the obvious answer, consistent with the
  approval store, with the same honest caveat: it raises the bar rather than closing the door.
- **Which model, and the accepted cost per generation.** Generation is once per automation, not
  once per run, so the lifetime cost is small — but it must be shown before the call, not after.
- **What happens to a generated plan.** It must go through validate → preview → approve exactly
  as a hand-written one does. A plan the application wrote is not more trustworthy than a plan the
  user wrote; if anything the argument runs the other way.

### Untested, and honestly so

- `send_keys` into an elevated window is refused rather than attempted, and the claim that it would
  fail *silently* remains received wisdom. No elevated GUI window was available to confirm it.
- Multi-monitor behaviour is arithmetically correct and exercised on one display only.
- The `foreach` + `show_picker` combination has not been run over a list large enough to test the
  overlay's scrolling under real data.

## Risk register

| Risk | Likelihood | Mitigation |
|---|---|---|
| Authoring friction means you stop creating automations | **High** | `validate` + `explain` CLI verbs, watched folder, schema/capabilities generated to disk, `CLAUDE.md`. Goal 1's exit criterion tests exactly this. |
| TOFU gate implemented as naive HMAC-refuse, breaking the workflow | Medium | Called out as challenge 2; control 4 specifies load-disabled-and-prompt, not refuse. Test it in Goal 2's exit criterion. |
| Defender / EDR flags the app | **High** | Expected and largely unavoidable for this class of app. Document it; code signing only matters if you distribute. |
| Elevated-window input fails silently | High | Detect integrity mismatch, report as a named failure reason. |
| Scope creep into UIA / Inspect Mode / in-app planner | **High** | All explicitly V2. The concept's own best examples need none of them. |
| DSL churn breaks saved automations | Medium | `schemaVersion` + a migration path written before v2 of the schema exists. Regression corpus catches it. |
| Schema drifts out of the structured-output subset, blocking V2 | Medium | CI asserts no `$ref` cycles and draft-2020-12 validity; constraint-layer tests assert nothing unexpressible leaked into the schema. Caught in V1, not V2. |
| V2 API mode leaks paths/titles in prompts | Medium | Control 7 — redaction at the boundary is a V2 gate, not a follow-up. |

---

## Verification

How to know each layer works, in the order the phases produce it.

1. **Core (no desktop needed).** Three distinct test groups:
   - **Conformance** — every `[JsonDerivedType]` discriminator exists in the schema's `oneOf`,
     and every schema action type has a matching record with matching required properties.
     Both directions, so neither side can drift.
   - **Validator** — every malicious/malformed case is rejected: unknown action type, unknown
     field, path outside allowed roots, `argv` containing shell metacharacters, nesting past
     depth 3, variable used before assignment, type mismatch on a `${…}` reference. Assert
     which *layer* rejects each one, so a constraint doesn't silently migrate from policy into
     the schema (or vanish).
   - **Contract freshness** — owned by `tools/schema-checks/`, not by the C# tests:
     `gen_capabilities.py --check` fails if `docs/capabilities.md` has drifted from the schema,
     and `check_schema.py` asserts valid draft 2020-12, no `$ref` cycles, and no keywords
     outside the structured-output subset. That last one is the V2 precondition, gated in V1 so
     it cannot regress.
2. **Engine (needs a desktop session).** Integration tests driving the executor against
   Notepad and Explorer: launch, verify window appears, send keys, verify clipboard, confirm
   timeout and `onError: abort` both fire.
3. **Safety.** Adversarial tests: does the panic key release held modifiers? Does the step cap
   trigger? Does `type_text` refuse when a UAC dialog is foreground? Does an *unsigned* file
   load disabled rather than either running or being rejected? Does a *modified signed* file
   drop back to unverified?
4. **Authoring loop.** The real V1 acceptance test, and it's a manual one: in a fresh Claude
   Code session with only `CLAUDE.md` and the generated docs, author a novel automation you
   haven't written before. Count the validate-fix cycles needed. More than three or four means
   `capabilities.md` is underspecified — fix the docs, not the prompt.
5. **Regression suite.** `dotnet run --project tools/RegressionSuite` — all golden plans still
   validate and still execute in the VM snapshot.
6. **End to end, on a clean VM.** Install, reboot, confirm the agent autostarts and hotkeys
   register. Author an automation externally, approve it, test it, break it deliberately,
   export the repair bundle, fix it, review the diff, restore the prior version. That full
   round trip is V1's acceptance test.

---

## Open items

- ~~The executor must re-check allowed roots at run time.~~ **Done** —
  `Engine/Execution/PathGuard.cs` checks every resolved path immediately before the operation,
  and `SafetyControlTests` covers the case the validator cannot: a path assembled from a picker
  result that traverses out of the allowed roots. Safety control 2 is now whole. Note the guard
  also covers `path_exists` postconditions, so a plan cannot use verification as a way to probe
  outside its roots.

- **Real requests have started hitting the edges of the primitive set.** The plan says grow it
  only when a real request needs one; three have now arrived, and they are not all the same kind
  of gap:

  | Request | Gap | Severity |
  |---|---|---|
  | Capture clipboard to a note | no `write_file` | missing primitive |
  | Open YouTube / open a terminal | none — expressible today | ✅ |
  | Wallpaper slideshow *(existing AHK script)* | no `set_wallpaper`, **and no state between runs** | missing primitive **and** a design gap |

  `write_file` is straightforward: constrain it to the allowed roots, refuse overwriting without
  confirmation, and bar it from the automations folder outright, or a plan could rewrite its own
  trigger and defeat safety control 4.

  **The slideshow is the interesting one.** "Next wallpaper" means remembering which image was
  used last, and the DSL has no state that survives a run — variables are born and die inside one
  execution. That is a deliberate property (it is what makes a plan a pure function of its inputs,
  and what makes it safe to re-run) so adding persistent state is not a small change; it would
  need its own design, with a story for what happens when a plan is edited or rolled back. Worth
  deciding on purpose rather than discovering halfway through implementing a slideshow.

  **Deliberately not added yet.** All three are scope decisions, not scaffolding.

- **Repair bundle format.** Plain Markdown block for pasting, versus a file that `cli` writes
  and Claude Code reads. The file is lower-friction; the clipboard is more obvious. Could ship
  both cheaply.
- **V2 model tier.** Haiku 4.5 at ~2¢/automation may well be sufficient for a constrained
  structured-output task. Worth an A/B against Opus 5 on the regression corpus once it exists,
  rather than assuming the top tier is needed.

# Hotkey AI

Windows tray app: a global hotkey fires a validated automation plan, which the engine
executes, verifies, and can roll back. See `PLAN.md` for the full design and `Concept.txt`
for the original product concept.

**Stack:** C# / .NET 10 (LTS), WPF. Two processes — `HotkeyAI.Agent` (tray, always resident,
owns the hotkey pump) and `HotkeyAI.Ui` (launched on demand, talks to the agent over a named
pipe). `HotkeyAI.Core` targets plain `net10.0` with no Windows dependencies.

## The one thing to understand

The LLM plans; the application executes. Those are separate, always. A plan is a **validated
JSON artefact**, never generated code — which is what makes it previewable, diffable,
versionable, and safe to run on a keypress.

**V1 has no planner in the app.** Automations are authored *here*, in Claude Code, against the
schema, and the app validates and runs them. V2 adds an in-app API planner using the same
schema for structured output. So authoring an automation is a normal task in this repo.

## Authoring an automation

The contract is `schema/hotkeyai-dsl-v1.schema.json` — draft 2020-12, and authoritative. Read
it before writing a plan; do not infer the shape from the examples alone. `docs/capabilities.md`
is the readable summary of the same thing.

1. Read the schema, particularly the `description` on each action and property. They state the
   real constraints, including the numeric bounds the schema itself cannot express.
2. Write the plan to `examples/<name>.json` (or the live automations folder once the agent
   exists).
3. **Validate before claiming it works:**
   ```powershell
   dotnet run --project src/HotkeyAI.Cli -- validate <file>
   dotnet run --project src/HotkeyAI.Cli -- validate <file> --json   # for a fix loop
   ```
   Errors carry a JSON Pointer and say what to change. Exit codes: `0` valid, `1` invalid,
   `2` bad usage or unreadable file — so a loop can branch on them without parsing output.
4. **Check the plan says what you meant**, not just that it parses:
   ```powershell
   dotnet run --project src/HotkeyAI.Cli -- explain <file>
   ```
   This prints the same preview the UI will show, including which actions are `(unverified)`.
   An automation that validates but explains wrongly is still wrong.
5. Iterate on validator errors until clean. Do not hand-wave a plan as correct without running
   the validator — schema validity is cheap to check and expensive to guess at.

### Rules that matter when writing plans

- **Follow the execution hierarchy.** Native API → app CLI/arguments → UI Automation →
  synthetic input, in that order. Prefer `launch_process` with `argv` over focusing a window
  and sending keystrokes. Prefer `send_appcommand` over focusing a media player. `send_keys` is
  a last resort — it cannot reach elevated windows and fails *silently* there.
- **Prefer `app` over `path`** on `launch_process`. Naming a logical application lets the engine
  resolve the executable from its registry, which is what makes a plan survive machine changes
  and app updates.
- **Add an `expect` wherever it is meaningful.** An action with no postcondition is reported to
  the user as unverified. Only five checks are verifiable — see `Postcondition` in the schema.
  Don't invent verification that isn't real.
- **Declare every variable** in `variables` before using it, with the right type. The policy
  layer checks dataflow: reading an undeclared variable, reading one nothing ever writes, or
  writing the wrong type (a `list_directories` produces a `pathList`, and picking from one
  yields a `path`, not `text`) are all errors.
- **Respect the numeric bounds** stated in each property's `description`. The schema cannot
  encode them — they are enforced by the policy layer, which is why they are written down.
- **`launch_process` with a `path` needs an allowed root**, and a path built from a variable is
  refused because it cannot be checked before the plan runs. Prefer `app`.
- **Nesting:** three action levels. `if`/`foreach` may contain one more `if`/`foreach`, and that
  inner one may contain leaf actions only. Deliberate limit — keeps plans statically analysable
  and the schema non-recursive.
- **Never put secrets in a plan.** Plans are plain JSON on disk.
- **`argv` is a list of separate arguments**, never a command line. No quoting, no escaping, no
  shell metacharacter meaning. There is deliberately no shell primitive.

## Repo layout

```
schema/     hotkeyai-dsl-v1.schema.json   the contract, hand-authored, source of truth
docs/       capabilities.md                generated from the schema; do not hand-edit
examples/   reference automations          also the regression corpus + first-run examples
tools/      schema-checks/                 Python schema hygiene + example validation
src/        HotkeyAI.Core                  DSL, schema, validators — no Windows deps
            HotkeyAI.Engine                executor + safety controls, against IDesktop
            HotkeyAI.Windows               Win32 IDesktop -- the only project using Win32
            HotkeyAI.Cli                   validate / explain / schema / apps / run
            HotkeyAI.Agent                 resident host: hotkeys, panic key, approvals
            HotkeyAI.Ui                    plan preview, picker overlay (not yet built)
tests/      HotkeyAI.Core.Tests            conformance, validators, error quality
            HotkeyAI.Engine.Tests          safety controls and execution, via FakeDesktop
```

## Conventions

- **Schema first.** Adding a primitive means: schema (with a written `description`) → the C#
  record → regenerate `docs/capabilities.md`. Never the other way round; a bidirectional
  conformance test fails if the records and schema disagree.
- **Descriptions are prompt material.** Every `description` in the schema is read by a planner.
  Write them for a model that has never seen this DSL, and state the policy bounds there since
  the schema can't encode them.
- **Keep the schema inside the structured-output subset** — no `minimum`/`maxLength`/`if`/`not`.
  Those constraints belong in the policy validator so V2 can hand the schema to the API
  unchanged. `tools/schema-checks/check_schema.py` enforces this.
- **`HotkeyAI.Core` and `HotkeyAI.Engine` stay free of Windows dependencies.** The engine
  reaches the OS only through `IDesktop`, which is what makes the safety controls testable —
  step caps, the panic key, the sensitive-window guard and the path guard all have tests that
  run on Linux CI. Anything touching Win32 belongs in `HotkeyAI.Agent`. If the CI build ever
  needs a Windows runner, something has leaked.
- **Adding a primitive means teaching the executor too**, not just the schema and the renderer.
  `PlanExecutor.DispatchAsync` has a case per action; the fallback returns a failure naming the
  omission rather than silently doing nothing.
- Safety controls (panic key, step caps, sensitive-window guard, trust-on-first-use signing)
  are requirements, not polish. See `PLAN.md` § Non-negotiable safety controls before touching
  the executor or the store.

## Commands

```powershell
# authoring an automation
dotnet run --project src/HotkeyAI.Cli -- validate examples/my-plan.json
dotnet run --project src/HotkeyAI.Cli -- validate examples/my-plan.json --json
dotnet run --project src/HotkeyAI.Cli -- explain  examples/my-plan.json
dotnet run --project src/HotkeyAI.Cli -- schema           # print the contract

# whole-repo checks (both run in CI)
python tools/schema-checks/check_schema.py schema/hotkeyai-dsl-v1.schema.json
python tools/schema-checks/validate_examples.py .
python tools/schema-checks/gen_capabilities.py . --check
dotnet build
dotnet test
```

Adding a primitive means all of: schema (with a `description`) → the C# record and its
`[DslType]` → a case in `PlanRenderer.DescribeAction` → an example that exercises it →
regenerate `docs/capabilities.md`. Every one of those is gated by a test or a CI check, so
skipping a step fails the build rather than shipping a half-added primitive.

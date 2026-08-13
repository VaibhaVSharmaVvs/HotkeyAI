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
   python tools/schema-checks/validate_examples.py .
   ```
   Once the .NET solution exists, `HotkeyAI.Cli validate <file>` is the equivalent and also
   runs the policy layer.
4. Iterate on validator errors until clean. Do not hand-wave a plan as correct without running
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
- **Declare every variable** in `variables` before using it, with the right type. Writing a
  `pathList` and reading it as `path` is a validation error.
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
src/        HotkeyAI.Core|Agent|Ui|Cli     (Core has no Windows dependencies)
tests/      HotkeyAI.Core.Tests            conformance, validator, policy layer
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
- **`HotkeyAI.Core` stays free of Windows dependencies** so it is testable without a desktop
  session.
- Safety controls (panic key, step caps, sensitive-window guard, trust-on-first-use signing)
  are requirements, not polish. See `PLAN.md` § Non-negotiable safety controls before touching
  the executor or the store.

## Commands

```powershell
python tools/schema-checks/check_schema.py schema/hotkeyai-dsl-v1.schema.json
python tools/schema-checks/validate_examples.py .
dotnet build
dotnet test
```

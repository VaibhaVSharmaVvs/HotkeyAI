# Hotkey AI

Press a hotkey, run an automation. The automation is a validated JSON plan you can read before
it executes, version, and roll back — not generated code.

Windows only. Personal project. See **[PLAN.md](PLAN.md)** for the design and phasing,
**[Concept.txt](Concept.txt)** for the original product concept, and **[CLAUDE.md](CLAUDE.md)**
if you're authoring automations.

## Status

Goal 1 of 4, early. Nothing runs yet.

| | |
|---|---|
| ✅ | DSL schema — 25 primitives, hand-authored, validated, no `$ref` cycles |
| ✅ | Eight reference automations, **100% action-type coverage** |
| ✅ | Schema checks + generated `docs/capabilities.md`, gated in CI |
| ✅ | `HotkeyAI.Core` — 25 records, schema validator, bidirectional conformance test |
| ✅ | `HotkeyAI.Cli` — `validate` (with `--json`), `explain`, `schema` |
| ⬜ | `HotkeyAI.Core` — policy layer (numeric bounds, allowed roots, variable dataflow) |
| ⬜ | `HotkeyAI.Agent` — hotkey pump, executor, observer, store |
| ⬜ | `HotkeyAI.Cli` — `import` / `run` / `logs` (need the agent) |
| ⬜ | `HotkeyAI.Ui` — automation list, plan preview, picker overlay |

You can author and inspect automations today:

```powershell
dotnet run --project src/HotkeyAI.Cli -- explain  examples/project-launcher.json
dotnet run --project src/HotkeyAI.Cli -- validate examples/project-launcher.json --json
```

## How it works

```
hotkey pressed
      ↓
  validated plan  ──►  executor  ──►  observer: did the postcondition hold?
      ↑                                     │
  authored in Claude Code                    ├─ yes → done
  against schema/                           └─ no  → failure report → repair
```

Three ideas carry the design:

1. **The planner and the executor are separate.** A plan is data, so it can be validated,
   previewed, diffed, and rolled back. Generating a script instead would make all four
   impossible.
2. **There is an execution hierarchy.** Native API → app CLI → UI Automation → synthetic
   input. `launch_process("Code.exe", path)` beats twelve simulated UI steps, and a media key
   is a `WM_APPCOMMAND` broadcast rather than a click.
3. **AI is out of the hot path.** Planning happens once per automation; execution is pure
   engine. Cost and latency never scale with how often you press the key.

V1 has no in-app planner — automations are authored in Claude Code against the schema. V2 adds
an API planner using that same schema for structured output.

## Getting started

Requires the [.NET 9 SDK](https://dotnet.microsoft.com/download) (`winget install
Microsoft.DotNet.SDK.9`) and Python with `jsonschema` for the schema checks.

```powershell
python tools/schema-checks/check_schema.py schema/hotkeyai-dsl-v1.schema.json
python tools/schema-checks/validate_examples.py .
```

## A plan

```json
{
  "schemaVersion": 1,
  "name": "Project Launcher",
  "trigger": { "type": "hotkey", "keys": ["CTRL", "ALT", "P"] },
  "variables": [
    { "name": "candidates", "type": "pathList" },
    { "name": "selected", "type": "path" }
  ],
  "actions": [
    { "id": "s1", "type": "list_directories", "path": "…\\Projects", "into": "candidates" },
    { "id": "s2", "type": "show_picker", "source": "candidates", "into": "selected" },
    { "id": "s3", "type": "launch_process", "app": "vscode", "argv": ["${selected}"],
      "expect": { "type": "window_exists",
                  "selector": { "processName": "Code", "titleContains": "${selected.name}" } } }
  ]
}
```

Full examples in [`examples/`](examples/).

## A note on antivirus

This app registers global hotkeys, synthesises keyboard input, and spawns processes — which is
also what a keylogger does. Expect Defender or any EDR to flag it. That's a correct signal about
the *capability*, not a false positive to dismiss, and it's a reason to read the safety controls
in `PLAN.md` before trusting an automation you didn't author.

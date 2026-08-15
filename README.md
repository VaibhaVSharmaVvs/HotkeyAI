# Hotkey AI

Press a hotkey, run an automation. The automation is a validated JSON plan you can read before
it executes, version, and roll back — not generated code.

Windows only. Personal project. See **[PLAN.md](PLAN.md)** for the design and phasing,
**[Concept.txt](Concept.txt)** for the original product concept, and **[CLAUDE.md](CLAUDE.md)**
if you're authoring automations.

## Status

**V1 is a working MVP** — tagged [`v1.0.0`](../../releases/tag/v1.0.0). A hotkey fires a
validated plan, the engine runs and verifies it, and the agent sits in the tray. Every one of the
25 primitives has executed against Win32 on a real desktop.

| | |
|---|---|
| ✅ | DSL schema — 25 primitives, hand-authored, validated, no `$ref` cycles |
| ✅ | Eight reference automations, **100% action-type coverage** |
| ✅ | Schema checks + generated `docs/capabilities.md`, gated in CI |
| ✅ | `HotkeyAI.Core` — 25 records, schema validator, bidirectional conformance test |
| ✅ | `HotkeyAI.Core` — policy layer (bounds, allowed roots, variable dataflow) |
| ✅ | `HotkeyAI.Core` — fuzzy ranking and the authoring prompt, both unit tested |
| ✅ | `HotkeyAI.Engine` — executor, observer, safety controls, all 25 primitives |
| ✅ | `HotkeyAI.Engine` — store with the trust-on-first-use gate and enable/disable |
| ✅ | `HotkeyAI.Windows` — Win32 `IDesktop`: processes, windows, input, files, clipboard |
| ✅ | `HotkeyAI.Windows` — hotkey pump with `MOD_NOREPEAT` and honest failure reporting |
| ✅ | `HotkeyAI.Windows` — DPAPI approvals, autostart, monitor and foreground helpers |
| ✅ | `HotkeyAI.Ui` — picker, input, confirm and toast overlays |
| ✅ | `HotkeyAI.Ui` — tray icon and menu, and the dashboard |
| ✅ | `HotkeyAI.Agent` — tray host, panic key, single-instance guard, daily log |
| ✅ | `HotkeyAI.Agent` — autostart at login, and hotkey registration history |
| ✅ | `HotkeyAI.Cli` — `validate`, `explain`, `schema`, `apps`, `run`, `list`, `approve`, `autostart` |
| ✅ | Hotkey capture in the dashboard, with a live availability check |
| ⬜ | Folder watcher — automations reload when you ask, not automatically |
| ⬜ | `HotkeyAI.Cli` — `import` / `logs` |
| ⬜ | V2 — the in-app API planner |

See **[Known gaps](PLAN.md#known-gaps-at-v1)** for what is missing and why it matters.

Author, inspect and run automations:

```powershell
dotnet run --project src/HotkeyAI.Cli -- explain  examples/project-launcher.json
dotnet run --project src/HotkeyAI.Cli -- validate examples/project-launcher.json --json
dotnet run --project src/HotkeyAI.Cli -- apps                    # what resolves here
dotnet run --project src/HotkeyAI.Cli -- run examples/project-launcher.json --dry-run
dotnet run --project src/HotkeyAI.Cli -- run examples/project-launcher.json --ui
```

Then install the agent, which is what makes hotkeys live. It is a windowed process, so anything
console-shaped is a CLI verb:

```powershell
dotnet publish src/HotkeyAI.Agent -c Release -o "$env:LOCALAPPDATA\HotkeyAI\app"
dotnet publish src/HotkeyAI.Cli   -c Release -o "$env:LOCALAPPDATA\HotkeyAI\app"

$app = "$env:LOCALAPPDATA\HotkeyAI\app"
& "$app\hotkeyai.exe" list              # state of every automation
& "$app\hotkeyai.exe" approve            # read each plan, then approve
& "$app\hotkeyai.exe" autostart on       # start at login
& "$app\hotkeyai-agent.exe"              # register hotkeys and listen
```

The agent appears in the notification area. Windows 11 hides new tray icons behind the chevron —
drag it onto the taskbar to pin it. Left-click opens the dashboard; right-click gives the menu.

Nothing runs until you have read the plan and approved it. Approval is granted against the
file's contents, so editing an automation makes it inert again until you re-approve. Switching an
automation off is separate and does not withdraw approval.

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
   input. `launch_process("Code.exe", path)` beats twelve simulated UI steps, and play/pause is
   a media-key press the shell routes to whoever owns playback — no window to find at all.
3. **AI is out of the hot path.** Planning happens once per automation; execution is pure
   engine. Cost and latency never scale with how often you press the key.

V1 has no in-app planner — automations are authored in Claude Code against the schema. V2 adds
an API planner using that same schema for structured output.

## Getting started

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) (`winget install
Microsoft.DotNet.SDK.10`) and, for the schema checks, Python with `jsonschema`.

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

## Use one hotkey manager

Hotkey AI expects to be the only global hotkey manager running. Windows hands a combination to
whichever application asks for it first and tells the loser nothing useful, so a second manager —
AutoHotkey, a vendor utility, a gaming overlay — produces shortcuts that silently stop working,
on either side, with no way to tell which app is holding what.

If you already run AutoHotkey for this, the answer is to retire the scripts that collide rather
than to run both. Not knowing AutoHotkey is the reason this exists.

## A note on antivirus

This app registers global hotkeys, synthesises keyboard input, and spawns processes — which is
also what a keylogger does. Expect Defender or any EDR to flag it. That's a correct signal about
the *capability*, not a false positive to dismiss, and it's a reason to read the safety controls
in `PLAN.md` before trusting an automation you didn't author.

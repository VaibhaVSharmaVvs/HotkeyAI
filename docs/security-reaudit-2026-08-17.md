# Security re-audit — 2026-08-17 (round 2)

Re-attack of the fixes applied for the 2026-08-17 review (the 20 commits `4ecb052`…`b94e026`,
H1–H5, M1–M9, L1–L11). Same working directory, tests green before and after
(61 + 472 + 182 = 715 passing). This round attacked the *new* controls from a plan-author's seat
rather than re-reading the old code.

**Verdict:** 23 of 25 fixes hold under attack. **Two do not fully hold** — one is a bypass of the
new `open_path` control (finding A, code execution), one is an incomplete redaction (finding B).
Both are in the just-added code, both are reproduced end-to-end below, and both have a fix that is
a few lines in `Core`.

---

## A — `open_path` executable blocklist bypassed by a trailing dot or space (High)

**Defeats M9, and the trailing-space form also defeats H1.**

The M9 fix (`src/HotkeyAI.Core/Policy/ShellOpen.cs`) refuses `open_path` on a set of executable
extensions, so the shell cannot be handed something it will run. The check is
`ShellOpen.IsAllowed`, which reads the extension via `WindowsPath.Extension`
(`src/HotkeyAI.Core/Policy/WindowsPath.cs:153`) and looks it up in a fixed `HashSet`.

Windows strips trailing dots and spaces from the final path component before it acts on a path;
`WindowsPath.Extension` does not. So the two disagree:

| path in the plan | `WindowsPath.Extension` sees | blocklist verdict | what Windows runs |
|---|---|---|---|
| `pwn.bat`  | `.bat` | **refused** ✓ | — |
| `pwn.bat.` | `null` (dot is last char) | **allowed** ✗ | `pwn.bat` |
| `pwn.bat ` | `.bat ` (≠ `.bat`) | **allowed** ✗ | `pwn.bat` |

Reproduced end-to-end through the CLI, with a `.bat` that writes a marker so execution is not in
doubt (`scratchpad/attack_final.ps1`):

```
===== trailing-dot spelling =====
validate : VALIDATES
preview  : 1. [a1] Open …\final\pwn.bat. with its default application
run      : open_path: Succeeded (unverified) — Opened …\final\pwn.bat..
executed : True          <- the batch ran; marker written

===== trailing-space spelling =====
validate : VALIDATES
preview  : 1. [a1] Open …\final\pwn.bat  with its default application
run      : open_path: Succeeded (unverified) — Opened …\final\pwn.bat .
executed : True
```

Both spellings validate, pass the runtime `ShellOpen` check, and execute. This is exactly the
condition ShellOpen's own doc comment says it prevents: "It stops the shell from being asked to
*execute* something."

**Why the trailing-space form is worse.** The preview renders `Open …\pwn.bat  with its default
application` — the trailing space is invisible to the person approving the plan. So it defeats both
controls at once: the executable-blocklist (M9) *and* the disclosure guarantee H1 was written to
provide, since what the approver reads is indistinguishable from opening a harmless `pwn.bat`.

**Scope.** This is a plan-authoring bypass — the attacker writes the path string. It is *not*
reachable through the `list_files → foreach → open_path` shape M9 also worried about: enumeration
returns the real on-disk name, and a file literally named `drop.bat.` fails to launch because
Windows strips the dot and then cannot find it (verified, `scratchpad/attack_enum.ps1`). The live
vector is a path string the author controls, which is precisely what the blocklist is meant to be
robust against — the control exists so an *approved* plan cannot turn `open_path` into execution,
because the preview says "Open X" and X is not scrutinised.

**Fix.** In `WindowsPath.Extension` (or a small canonicalisation step ahead of it), trim trailing
dots and spaces from the final segment before extracting the extension, matching what Win32 does
to the path anyway. `pwn.bat.` and `pwn.bat ` then both read as `.bat` and are refused. The fix is
in `Core`, unit-testable on Linux CI, and the same `WindowsPath.Extension` is shared by the static
validator and the runtime executor, so one change closes both. A test for
`Extension("x.exe.") == ".exe"` and `Extension("x.exe ") == ".exe"` pins it. Worth also trimming in
`PathGuard` generally, so a trailing-dot spelling cannot mask a path anywhere else it is compared.

---

## B — Tainted clipboard / prompt text still reaches the transcript (Medium)

**M8 incompletely fixed.**

M8's fix marks `get_clipboard` and `show_input` results as "from outside the plan"
(`Variables.SetTextFromOutsideThePlan`) and redacts them in `InterpolateForLog`, which
`abort.reason` now uses. But `abort.reason` was the only field switched to `InterpolateForLog`.
Three action handlers still interpolate their path with the plain `Interpolate` and then put the
result in a logged detail — in the success line *and* in the guard's refusal message:

- `PathExistsAsync` — `src/HotkeyAI.Engine/Execution/PlanExecutor.Actions.cs:412,416` and the
  success detail `"{path} exists/does not exist"`.
- `OpenAsync` — `:428,432`.
- `ListAsync` — `:400` and `"Found N item(s) in {path}"`.

The refusal path is the general leak: it fires for *any* value that is not a valid in-root path, so
clipboard text that is not a path at all is echoed verbatim. Reproduced
(`scratchpad/attack_m8_leak.ps1`) — clipboard set to a fake credential, then read back through
`path_exists ${c}`:

```
[a1] get_clipboard: Succeeded (unverified) — Read 58 character(s) from the clipboard.
[a2] path_exists: Failed — Refused to check: "CORP-SECRET aws=AKIAI0SFODNN7EXAMPLE pw=hunter2-1464655627" is not an absolute path.

FAILED: Refused to check: "CORP-SECRET aws=AKIAI0SFODNN7EXAMPLE pw=hunter2-1464655627" is not an absolute path.
   leaked: True
```

That transcript is what `DashboardHost.BuildRepairPrompt` pastes and what `AutomationRunner` hands
to `AgentLog.Raw` under the running agent — the same file M8 was about. (The on-disk-log check read
False here only because the `hotkeyai run` CLI verb prints the transcript rather than writing it to
the agent log; under the agent it is written.)

Provenance is already tracked, so the missing step is small. **Fix.** Build these path-bearing
details from a redacting interpolation when the source variable is tainted — either route the
`{path}` shown in the detail (not the one handed to the guard/OS) through `InterpolateForLog`, or
have `PathGuard` refusal messages quote a redacted rendering. The value the guard *checks* must
stay the real one; only the value that gets *logged* needs redacting.

---

## What held up under re-attack

Each of these was attacked, not just re-read:

- **H1 (preview truncation)** — a 112-char `type_text` now renders in full as an indented block
  with a `! …shell characters…` warning when it contains newlines or shell metacharacters. The only
  gap is finding A's trailing space, which is a path, not a payload.
- **H2 (link containment)** — a directory junction to `System32` inside the profile is now resolved
  at runtime and refused for both `launch_process` and `open_path`, with the message naming the real
  target (`…is a link to "C:\Windows\System32\cmd.exe", which is outside the allowed roots`).
  `scratchpad/attack_h2_runtime.ps1`. (Static `validate` still passes it — expected; the runtime
  `PathGuard` is the boundary, and it holds.)
- **H4 (panic key)** — `Ctrl+Alt+Shift+Esc` as a trigger is now rejected by the shared
  `HotkeyChord.Problems`, so hand-authored JSON is refused the same as the UI. Confirmed via the
  adversarial corpus (`t01` → rejected).
- **H5 (app resolution)** — `ResolveForLaunch` is wired into `WindowsProcesses.ResolveAsync`, HKLM
  is consulted before HKCU, and a resolved path outside the trusted install roots is refused.
- **M1/M2 (vacuous verification, nested dataflow)** — `${ghost}` inside an `expect` or a predicate
  is now rejected by the validator, and a clipboard `contains:"${ghost}"` postcondition fails at
  run time instead of reporting "(verified)". Corpus `t02`, `t03`, `t12` → rejected.
- **M3 (loop scope)** — reading a `foreach` item after the loop is now rejected. Corpus `t04`.
- **M5 (time cap)** — a single long action is bounded by the remaining run budget, not just the gaps
  between actions.
- **M7 (mid-send re-check)** — `type_text` re-runs the hazard guard and a foreground-identity check
  every 32 characters; `send_keys` re-checks each repeat. The browser/WPF password-field gap is
  openly documented rather than papered over.
- **L1** — out-of-root literal paths on `open_path`, `list_files`, `path_exists` and
  `workingDirectory` are now rejected statically. Corpus `t29`, `t35`.

---

## Reproducing

```powershell
dotnet build src/HotkeyAI.Cli
# finding A — both spellings, with a marker-writing payload:
pwsh scratchpad/attack_final.ps1
# finding B — clipboard leak via path_exists refusal:
pwsh scratchpad/attack_m8_leak.ps1
# H2 holds at runtime:
pwsh scratchpad/attack_h2_runtime.ps1
```

The scripts live under the session scratchpad; the two that matter (`attack_final.ps1`,
`attack_m8_leak.ps1`) are self-contained and clean up after themselves. As with round 1, I did not
commit probe tests that assert the buggy behaviour — each finding states the exact observation so
it can become a regression test with the assertion inverted:

- Finding A: `WindowsPath.Extension("x.exe.")` and `("x.exe ")` should both return `.exe`; a
  `ShellOpen`/`open_path` test should refuse `pwn.bat.` and `pwn.bat `.
- Finding B: a run of `get_clipboard` → `path_exists ${c}` with a non-path clipboard value should
  produce a transcript that does **not** contain the clipboard text.

## Suggested order

1. **Finding A first** — it is code execution and the fix is one function in `Core`.
2. **Finding B** — small, and it is the same control (M8) the last round was meant to close.

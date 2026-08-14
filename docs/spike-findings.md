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

## Methodology note

The first version of the availability sweep reported all 26 `CTRL+SHIFT` combinations free —
including `CTRL+SHIFT+W`, which a separate script had just shown to be taken, reproducibly. The
cause was that PowerShell variable names are case-insensitive, so the accumulator `$vk` and the
lookup table `$VK` were the same variable: assigning `$vk = 0` destroyed the table and every key
resolved to `0`, which registers successfully and means nothing.

It was caught only because the result contradicted a known-true datum. A spike that agrees with
your expectations is the one most worth cross-checking, since a spike exists precisely to be
believed.

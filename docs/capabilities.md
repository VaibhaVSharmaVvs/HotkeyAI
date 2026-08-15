<!-- GENERATED FILE — do not edit.
     Source: schema/hotkeyai-dsl-v1.schema.json
     Regenerate: python tools/schema-checks/gen_capabilities.py . -->

# Capabilities

Every action Hotkey AI can perform. This is generated from
[`schema/hotkeyai-dsl-v1.schema.json`](../schema/hotkeyai-dsl-v1.schema.json), which is the
authoritative contract — when something here is ambiguous, read the schema.

## Choosing an action

Follow the execution hierarchy. Each step is more reliable than the one below it:

1. **A native API or process argument** — `launch_process` with `argv`, `open_path`,
   `send_appcommand`. Always prefer this.
2. **A window operation** — `focus_window`, `move_window`, `close_window`. Reliable, but
   depends on finding the right window.
3. **Synthetic keyboard input** — `send_keys`, `type_text`. Last resort. Cannot reach windows
   running elevated, where it fails *silently*.

There are deliberately no mouse or UI-element actions in this version, and no shell
primitive. If a task seems to need one, the plan is probably fighting the hierarchy.

## Common fields

Every action accepts these in addition to its own parameters:

| Field | Type | Description |
|---|---|---|
| `id` | `string` | Stable identifier for this action, unique within the automation. Used in execution logs, failure reports and plan diffs, so keep it stable when editing an existing plan. Convention: 's1', 's2', ... |
| `comment` | `string` | Optional note about why this action exists. Shown in the plan preview. |
| `timeoutMs` | `integer` | Maximum time this action may take before it is treated as failed. Policy bound: 100 to 300000. Also note the whole-automation caps of 200 actions and 120 seconds. |
| `onError` | `abort` \| `continue` | What to do if this action fails or its postcondition is not met. 'abort' stops the automation and reports the failure; 'continue' logs it and proceeds to the next action. Defaults to 'abort'. |
| `expect` | `Postcondition` | A machine-checkable statement about the state of the system after an action completes. Only these five checks can be verified. An action with no 'expect' is reported to the user as unverified — prefer adding one wherever it is meaningful. |

Not every action accepts all of them — `wait` and `abort` take no `expect`, and control-flow
actions take neither `expect` nor `timeoutMs`. The schema is precise about this.

## Process

### `launch_process`

Start an application. Prefer 'app' over 'path': naming a logical application lets the engine resolve the real executable from its registry of known install locations, which is what makes this robust across machines and updates. Use 'path' only for an executable the registry does not know.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `app` | `string` | one of | Logical application name resolved by the engine's app registry. Known values include: vscode, explorer, chrome, edge, firefox, terminal, powershell, notepad, spotify, slack, teams, discord, cursor, outlook, obsidian. If the app you need is absent, use 'path'. |
| `path` | `string` | one of | Absolute path to an executable. Must sit under one of the configured allowed roots — the policy validator rejects anything else. |
| `argv` | list of `string` | no | Arguments as a list of separate strings, never one command line. Each element is passed as a single argument, so no quoting or escaping is needed and shell metacharacters have no special meaning. |
| `workingDirectory` | `string` | no | Working directory for the new process. Defaults to the executable's own directory. |

Supply exactly one of: `app`, `path`.

### `terminate_process`

Close a running process. This is a destructive action: the application prompts the user for confirmation the first time an automation uses it. Prefer close_window, which lets the app save state and shut down cleanly.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `processName` | `string` | yes | Process name without .exe. |
| `force` | `boolean` | no | Kill without waiting for a clean exit. Unsaved work is lost. Defaults to false. |

### `wait_for_process`

Block until a process is running. Use after launching something slow, when the next action depends on it being up.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `processName` | `string` | yes | Process name without .exe. |

## Window

### `focus_window`

Bring a window to the foreground and give it keyboard focus. Required before send_keys or type_text unless the target already has focus.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `selector` | `WindowSelector` | yes | Identifies a target window. Supply at least one matching field; when several are given, all must match. Prefer processName plus titleContains — it is the most reliable combination in practice, and the only one that reliably distinguishes windows of apps that open several. Two traps worth knowing: matching processName alone for 'explorer' also matches the desktop shell window, so pair it with a titleContains naming the folder; and a Store or UWP app such as Settings runs inside a shared host process, so its processName is the host's rather than the app's — match those on title. |

### `minimize_window`

Minimize a window.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `selector` | `WindowSelector` | yes | Identifies a target window. Supply at least one matching field; when several are given, all must match. Prefer processName plus titleContains — it is the most reliable combination in practice, and the only one that reliably distinguishes windows of apps that open several. Two traps worth knowing: matching processName alone for 'explorer' also matches the desktop shell window, so pair it with a titleContains naming the folder; and a Store or UWP app such as Settings runs inside a shared host process, so its processName is the host's rather than the app's — match those on title. |

### `maximize_window`

Maximize a window.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `selector` | `WindowSelector` | yes | Identifies a target window. Supply at least one matching field; when several are given, all must match. Prefer processName plus titleContains — it is the most reliable combination in practice, and the only one that reliably distinguishes windows of apps that open several. Two traps worth knowing: matching processName alone for 'explorer' also matches the desktop shell window, so pair it with a titleContains naming the folder; and a Store or UWP app such as Settings runs inside a shared host process, so its processName is the host's rather than the app's — match those on title. |

### `move_window`

Move or resize a window to a named position, optionally on a specific monitor. Named positions are used instead of pixel coordinates so a plan keeps working across different screen sizes and DPI settings.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `selector` | `WindowSelector` | yes | Identifies a target window. Supply at least one matching field; when several are given, all must match. Prefer processName plus titleContains — it is the most reliable combination in practice, and the only one that reliably distinguishes windows of apps that open several. Two traps worth knowing: matching processName alone for 'explorer' also matches the desktop shell window, so pair it with a titleContains naming the folder; and a Store or UWP app such as Settings runs inside a shared host process, so its processName is the host's rather than the app's — match those on title. |
| `position` | `left_half` \| `right_half` \| `top_half` \| `bottom_half` \| `maximized` \| `centered` \| `top_left_quarter` \| `top_right_quarter` \| `bottom_left_quarter` \| `bottom_right_quarter` | yes | Target layout position. |
| `monitor` | `string` | no | Which monitor to place the window on: 'primary', 'secondary', or a 1-based index as a string such as '2'. Defaults to the monitor the window is already on. |

### `close_window`

Ask a window to close, as if the user clicked its close button. The application may prompt to save. Prefer this over terminate_process.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `selector` | `WindowSelector` | yes | Identifies a target window. Supply at least one matching field; when several are given, all must match. Prefer processName plus titleContains — it is the most reliable combination in practice, and the only one that reliably distinguishes windows of apps that open several. Two traps worth knowing: matching processName alone for 'explorer' also matches the desktop shell window, so pair it with a titleContains naming the folder; and a Store or UWP app such as Settings runs inside a shared host process, so its processName is the host's rather than the app's — match those on title. |

### `wait_for_window`

Block until a matching window exists. Use after launching an application, before sending it any input.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `selector` | `WindowSelector` | yes | Identifies a target window. Supply at least one matching field; when several are given, all must match. Prefer processName plus titleContains — it is the most reliable combination in practice, and the only one that reliably distinguishes windows of apps that open several. Two traps worth knowing: matching processName alone for 'explorer' also matches the desktop shell window, so pair it with a titleContains naming the folder; and a Store or UWP app such as Settings runs inside a shared host process, so its processName is the host's rather than the app's — match those on title. |

## Input

### `send_keys`

Send one keyboard chord to whichever window currently has focus. Focus the target window first. This is a last resort: prefer launching with arguments, or send_appcommand for media keys, because synthetic input cannot reach windows running at a higher integrity level than this application and will silently do nothing there.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `keys` | list of enum (see schema) | yes | The chord to send, as modifiers followed by one non-modifier key. Example: ['CTRL','SHIFT','N']. |
| `repeat` | `integer` | no | How many times to send the chord. Policy bound: 1 to 50. Defaults to 1. |

### `type_text`

Type literal text into the focused window. The application refuses to run this when the foreground window is a password field, a credential prompt, or a UAC dialog. Never put secrets in a plan — plans are stored as plain JSON on disk.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `text` | `string` | yes | The text to type. May interpolate variables. |

### `send_appcommand`

Send a system-wide multimedia or browser command. This is the correct way to control media playback: it reaches whichever application owns playback without needing to find, focus, or click anything, and works even when that application is minimized. Use this rather than focusing a media player and sending keys to it.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `command` | `media_play_pause` \| `media_next_track` \| `media_previous_track` \| `media_stop` \| `volume_up` \| `volume_down` \| `volume_mute` \| `browser_back` \| `browser_forward` \| `browser_refresh` | yes | The command to broadcast. |

## Files

### `list_directories`

List subdirectories of a path into a variable of type pathList. Commonly paired with show_picker to let the user choose one.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `path` | `string` | yes | Directory to list. Must sit under a configured allowed root. |
| `depth` | `integer` | no | How many levels to descend. 1 means immediate children only. Policy bound: 1 to 5. Defaults to 1. |
| `into` | `string` | yes | Name of a declared pathList variable to receive the results. |

### `list_files`

List files under a path into a variable of type pathList.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `path` | `string` | yes | Directory to search. Must sit under a configured allowed root. |
| `pattern` | `string` | no | Glob pattern to match file names, for example '*.sln'. Defaults to all files. |
| `depth` | `integer` | no | How many levels to descend. Policy bound: 1 to 5. Defaults to 1. |
| `into` | `string` | yes | Name of a declared pathList variable to receive the results. |

### `path_exists`

Test whether a path exists, storing the answer in a boolean variable. Use with an 'if' action to branch.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `path` | `string` | yes | File or directory to test for. |
| `into` | `string` | yes | Name of a declared boolean variable to receive the result. |

### `open_path`

Open a file or folder with whatever application Windows associates with it, as if the user double-clicked it. Use this when the specific application does not matter; use launch_process with an 'app' when it does.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `path` | `string` | yes | File or folder to open. Must sit under a configured allowed root. |

## Clipboard

### `set_clipboard`

Replace the clipboard contents with text. Note this destroys whatever the user had copied.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `text` | `string` | yes | Text to place on the clipboard. |

### `get_clipboard`

Read the clipboard into a text variable. Clipboard contents are redacted from execution logs.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `into` | `string` | yes | Name of a declared text variable to receive the clipboard text. |

## Prompts

### `show_picker`

Show a fuzzy-search overlay listing the items in a list variable and store the user's choice. The automation pauses until the user picks or cancels; cancelling is treated as a failure, so set onError deliberately.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `source` | `string` | yes | Name of a declared pathList or textList variable holding the items to choose from. |
| `prompt` | `string` | no | Prompt shown above the list. Example: 'Open which project?' |
| `into` | `string` | yes | Name of a declared variable to receive the chosen item. Its type must match the element type of 'source' — path for a pathList, text for a textList. |

### `show_input`

Prompt the user to type a value and store it in a text variable. The automation pauses until the user submits or cancels.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `prompt` | `string` | yes | Prompt shown to the user. |
| `defaultValue` | `string` | no | Pre-filled value. |
| `into` | `string` | yes | Name of a declared text variable to receive the entered value. |

### `notify`

Show a transient toast notification. Does not block.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `message` | `string` | yes | Text shown in the toast. |
| `level` | `info` \| `warning` \| `error` | no | Visual severity. Defaults to 'info'. |

## Control

### `wait`

Pause for a fixed duration. Prefer wait_for_window or wait_for_process, which finish as soon as the condition holds instead of always burning the full delay. Use a fixed wait only when there is nothing observable to wait on.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `durationMs` | `integer` | yes | How long to pause. Policy bound: 10 to 30000. |

### `abort`

Stop the automation immediately and report the reason. Typically used inside an 'if' branch when a precondition does not hold.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `reason` | `string` | no | Message shown to the user explaining why the automation stopped. |

### `if`

Run one branch or the other depending on a condition. Actions inside the branches may nest one level further.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `condition` | `Condition` | yes | The test for an 'if' action. Either a single predicate, or all_of / any_of over a flat list of predicates. Conditions do not nest beyond that, which keeps every plan statically analysable. |
| `then` | list of `ActionL1` | yes | Actions to run when the condition holds. |
| `else` | list of `ActionL1` | no | Actions to run when the condition does not hold. Omit if there is nothing to do. |

### `foreach`

Run a body once per item in a list variable. The loop is bounded: it always iterates over an already-materialised list and never more than maxIterations times, so it cannot run away.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `source` | `string` | yes | Name of a declared pathList or textList variable to iterate. |
| `itemVariable` | `string` | yes | Name of a declared variable that holds the current item inside the body. Its type must match the element type of 'source'. |
| `maxIterations` | `integer` | no | Hard cap on iterations, applied even if the list is longer. Policy bound: 1 to 100. Defaults to 25. |
| `body` | list of `ActionL1` | yes | Actions to run once per item. Reference the current item as ${itemVariable}. |

## Postconditions (`expect`)

A machine-checkable statement about the state of the system after an action completes. Only these five checks can be verified. An action with no 'expect' is reported to the user as unverified — prefer adding one wherever it is meaningful.

| `type` | Fields | Checks |
|---|---|---|
| `process_running` | `processName`, `withinMs` |  |
| `window_exists` | `selector`, `withinMs` |  |
| `path_exists` | `path`, `withinMs` |  |
| `clipboard_matches` | `contains`, `equals`, `withinMs` | Supply one of `contains`, `equals`. |
| `foreground_process_is` | `processName`, `withinMs` |  |

## Conditions (`if`)

The test for an 'if' action. Either a single predicate, or all_of / any_of over a flat list of predicates. Conditions do not nest beyond that, which keeps every plan statically analysable.

| `type` | Fields | Holds when |
|---|---|---|
| `process_running` | `processName` | True when a named process is running. |
| `window_exists` | `selector` | True when a window matching the selector exists. |
| `path_exists` | `path` | True when a file or directory exists at the given path. |
| `variable_equals` | `variable`, `value` | True when a variable's value equals the given value, compared as text. |
| `variable_empty` | `variable` | True when a variable is unset, empty text, or an empty list. Useful after a show_picker or a list action that may have produced nothing. |
| `all_of` | `conditions` | True only when every listed predicate holds. |
| `any_of` | `conditions` | True when at least one listed predicate holds. |

Any predicate accepts `negate: true` to invert it. Conditions do not nest beyond
`all_of` / `any_of` over a flat list.

## Variable types

Declared type. The validator rejects a plan that writes or reads a variable at the wrong type — for example passing a 'text' variable where a 'path' is required.

- `text`
- `path`
- `pathList`
- `textList`
- `boolean`
- `integer`

A string that may interpolate variables using ${name} or ${name.property}. Available properties on a path value are: .name (file or folder name), .fullPath, .parent, .extension.

## Nesting

The ordered list of actions to execute. Execution stops at the first action that fails unless that action sets onError to 'continue'. Nesting rule: there are three action levels, so a control-flow action ('if' or 'foreach') may contain another control-flow action, but that inner one may contain leaf actions only. In other words at most two control-flow actions may be nested, and the deepest bodies hold plain actions. This is a deliberate limit, not an oversight — it keeps every plan statically analysable.


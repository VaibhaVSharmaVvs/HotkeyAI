"""Generate docs/capabilities.md from the DSL schema.

The schema is the source of truth; this is the readable projection of it that a planner
reads alongside it. Because it is generated, it cannot drift.

Usage:
    python tools/schema-checks/gen_capabilities.py .            # write docs/capabilities.md
    python tools/schema-checks/gen_capabilities.py . --check    # fail if out of date (CI)

The C# generator in HotkeyAI.Core must produce byte-identical output; this file is its spec.
"""
import json, sys
from pathlib import Path

# Grouping lives here rather than in the schema so the schema stays inside the
# structured-output subset (no custom keywords). check() below fails if a new action type
# is added without being categorised, so this cannot silently fall behind.
CATEGORIES = [
    ("Process",   ["launch_process", "terminate_process", "wait_for_process"]),
    ("Window",    ["focus_window", "minimize_window", "maximize_window", "move_window",
                   "close_window", "wait_for_window"]),
    ("Input",     ["send_keys", "type_text", "send_appcommand"]),
    ("Files",     ["list_directories", "list_files", "path_exists", "open_path"]),
    ("Clipboard", ["set_clipboard", "get_clipboard"]),
    ("Prompts",   ["show_picker", "show_input", "notify"]),
    ("Control",   ["wait", "abort", "if", "foreach"]),
]

repo = Path(sys.argv[1])
check_only = "--check" in sys.argv
schema = json.loads((repo / "schema" / "hotkeyai-dsl-v1.schema.json").read_text(encoding="utf-8"))
defs = schema["$defs"]


def deref(node):
    """Follow a single local $ref one hop."""
    if isinstance(node, dict) and "$ref" in node:
        ref = node["$ref"]
        if ref.startswith("#/$defs/"):
            return defs.get(ref.split("/")[-1], {})
    return node


def type_label(node):
    """Human-readable type for a property schema."""
    raw = node
    node = deref(node)
    if "const" in node:
        return f"`\"{node['const']}\"`"
    if "enum" in node:
        vals = node["enum"]
        if len(vals) > 12:
            return "enum (see schema)"
        return " \\| ".join(f"`{v}`" for v in vals)
    t = node.get("type")
    if t == "array":
        return f"list of {type_label(node.get('items', {}))}"
    if t == "object" or "oneOf" in node or "anyOf" in node:
        name = raw.get("$ref", "").split("/")[-1] if isinstance(raw, dict) else ""
        return f"`{name}`" if name else "object"
    if isinstance(raw, dict) and "$ref" in raw:
        name = raw["$ref"].split("/")[-1]
        if name in ("TemplateString", "VariableName", "ActionId", "ActionComment"):
            return "`string`"
        if name == "TimeoutMs":
            return "`integer`"
        if name == "Negate":
            return "`boolean`"
        return f"`{name}`"
    return f"`{t}`" if t else "any"


def describe(node):
    """A property's own description wins over the description of its $ref target.

    In draft 2020-12 a sibling `description` next to `$ref` is legal and more specific, so
    following the ref first would replace a purpose-written description with a generic one.
    """
    if isinstance(node, dict) and "description" in node:
        return node["description"].strip()
    return deref(node).get("description", "").strip()


# action type -> ($defs name) for the canonical (outermost) variant
leaf_names = [r["$ref"].split("/")[-1] for r in defs["LeafAction"]["oneOf"]]
action_def = {}
for name in leaf_names + ["IfL0", "ForEachL0"]:
    body = defs[name]
    const = body["properties"]["type"]["const"]
    action_def.setdefault(const, name)

known = {a for _, group in CATEGORIES for a in group}
missing = set(action_def) - known
extra = known - set(action_def)
if missing or extra:
    if missing:
        print(f"ERROR: action types not categorised in CATEGORIES: {sorted(missing)}")
    if extra:
        print(f"ERROR: CATEGORIES lists unknown action types: {sorted(extra)}")
    sys.exit(1)

SKIP = {"type"}
COMMON = {"id", "comment", "timeoutMs", "onError", "expect"}

out = []
w = out.append

w("<!-- GENERATED FILE — do not edit.")
w("     Source: schema/hotkeyai-dsl-v1.schema.json")
w("     Regenerate: python tools/schema-checks/gen_capabilities.py . -->")
w("")
w("# Capabilities")
w("")
w("Every action Hotkey AI can perform. This is generated from")
w("[`schema/hotkeyai-dsl-v1.schema.json`](../schema/hotkeyai-dsl-v1.schema.json), which is the")
w("authoritative contract — when something here is ambiguous, read the schema.")
w("")
w("## Choosing an action")
w("")
w("Follow the execution hierarchy. Each step is more reliable than the one below it:")
w("")
w("1. **A native API or process argument** — `launch_process` with `argv`, `open_path`,")
w("   `send_appcommand`. Always prefer this.")
w("2. **A window operation** — `focus_window`, `move_window`, `close_window`. Reliable, but")
w("   depends on finding the right window.")
w("3. **Synthetic keyboard input** — `send_keys`, `type_text`. Last resort. Cannot reach windows")
w("   running elevated, where it fails *silently*.")
w("")
w("There are deliberately no mouse or UI-element actions in this version, and no shell")
w("primitive. If a task seems to need one, the plan is probably fighting the hierarchy.")
w("")
w("## Common fields")
w("")
w("Every action accepts these in addition to its own parameters:")
w("")
w("| Field | Type | Description |")
w("|---|---|---|")
for f in ["id", "comment", "timeoutMs", "onError", "expect"]:
    node = defs["LaunchProcess"]["properties"].get(f)
    if node is None:
        continue
    w(f"| `{f}` | {type_label(node)} | {describe(node)} |")
w("")
w("Not every action accepts all of them — `wait` and `abort` take no `expect`, and control-flow")
w("actions take neither `expect` nor `timeoutMs`. The schema is precise about this.")
w("")

for cat, types in CATEGORIES:
    w(f"## {cat}")
    w("")
    for t in types:
        body = defs[action_def[t]]
        w(f"### `{t}`")
        w("")
        w(body.get("description", "").strip())
        w("")
        props = {k: v for k, v in body["properties"].items()
                 if k not in SKIP and k not in COMMON}
        if props:
            req = set(body.get("required", []))
            oneof_req = {r for branch in body.get("oneOf", []) for r in branch.get("required", [])}
            w("| Parameter | Type | Required | Description |")
            w("|---|---|---|---|")
            for pname, pbody in props.items():
                if pname in req:
                    r = "yes"
                elif pname in oneof_req:
                    r = "one of"
                else:
                    r = "no"
                w(f"| `{pname}` | {type_label(pbody)} | {r} | {describe(pbody)} |")
            if oneof_req:
                w("")
                w(f"Supply exactly one of: {', '.join(f'`{r}`' for r in sorted(oneof_req))}.")
        else:
            w("No parameters beyond the common fields.")
        w("")

w("## Postconditions (`expect`)")
w("")
w(defs["Postcondition"]["description"].strip())
w("")
w("| `type` | Fields | Checks |")
w("|---|---|---|")
for branch in defs["Postcondition"]["oneOf"]:
    p = branch["properties"]
    const = p["type"]["const"]
    fields = ", ".join(f"`{k}`" for k in p if k != "type")
    desc = branch.get("description", "").strip()
    if not desc:
        oneof_req = {r for b in branch.get("oneOf", []) for r in b.get("required", [])}
        desc = f"Supply one of {', '.join(f'`{r}`' for r in sorted(oneof_req))}." if oneof_req else ""
    w(f"| `{const}` | {fields} | {desc} |")
w("")

w("## Conditions (`if`)")
w("")
w(defs["Condition"]["description"].strip())
w("")
w("| `type` | Fields | Holds when |")
w("|---|---|---|")
for branch in defs["SimplePredicate"]["oneOf"]:
    p = branch["properties"]
    fields = ", ".join(f"`{k}`" for k in p if k not in ("type", "negate"))
    w(f"| `{p['type']['const']}` | {fields} | {branch.get('description','').strip()} |")
for branch in defs["Condition"]["oneOf"][1:]:
    p = branch["properties"]
    w(f"| `{p['type']['const']}` | `conditions` | {branch.get('description','').strip()} |")
w("")
w("Any predicate accepts `negate: true` to invert it. Conditions do not nest beyond")
w("`all_of` / `any_of` over a flat list.")
w("")

w("## Variable types")
w("")
w(deref(defs["VariableDeclaration"]["properties"]["type"]).get("description", "").strip())
w("")
for t in defs["VariableDeclaration"]["properties"]["type"]["enum"]:
    w(f"- `{t}`")
w("")
w(deref(defs["TemplateString"]).get("description", "").strip())
w("")

w("## Nesting")
w("")
w(schema["properties"]["actions"]["description"].strip())
w("")

text = "\n".join(out) + "\n"
target = repo / "docs" / "capabilities.md"

if check_only:
    current = target.read_text(encoding="utf-8") if target.exists() else ""
    if current != text:
        print(f"OUT OF DATE: {target} does not match the schema.")
        print("Run: python tools/schema-checks/gen_capabilities.py .")
        sys.exit(1)
    print(f"up to date: {target}")
else:
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(text, encoding="utf-8")
    print(f"wrote {target}  ({len(text):,} bytes, {len(out)} lines)")
    print(f"documented {len(action_def)} actions in {len(CATEGORIES)} categories")

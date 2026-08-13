"""Validate the reference automations against the DSL schema, and prove the schema
rejects the things it is supposed to reject.

The negative cases are the point: a schema that accepts everything is worthless as a
generation constraint. Each case below is a mistake a planner plausibly makes.
"""
import json, sys, copy
from pathlib import Path
from jsonschema import Draft202012Validator

repo = Path(sys.argv[1])
schema = json.loads((repo / "schema" / "hotkeyai-dsl-v1.schema.json").read_text(encoding="utf-8"))

Draft202012Validator.check_schema(schema)
print("schema is a valid draft 2020-12 document\n")
v = Draft202012Validator(schema)

# ---------- positive: the reference automations ----------
print("POSITIVE — reference automations must validate")
ok = True
examples = sorted((repo / "examples").glob("*.json"))
for f in examples:
    doc = json.loads(f.read_text(encoding="utf-8"))
    errs = sorted(v.iter_errors(doc), key=lambda e: list(e.absolute_path))
    if errs:
        ok = False
        print(f"  FAIL  {f.name}")
        for e in errs[:6]:
            loc = "/".join(str(x) for x in e.absolute_path) or "(root)"
            print(f"          at {loc}: {e.message[:160]}")
    else:
        n = len(doc.get("actions", []))
        print(f"  pass  {f.name:28} ({n} top-level action{'s' if n != 1 else ''})")

# ---------- negative: mistakes a planner actually makes ----------
base = json.loads((repo / "examples" / "project-launcher.json").read_text(encoding="utf-8"))

def mutate(fn):
    d = copy.deepcopy(base)
    fn(d)
    return d

def nest(control_depth):
    """Wrap a leaf action in `control_depth` nested control-flow actions.

    The schema permits three ACTION levels (L0 -> L1 -> L2) where L2 is leaves only,
    so control_depth 1 and 2 are legal and 3 is not.
    """
    node = {"id": "leaf", "type": "notify", "message": "hi"}
    for i in range(control_depth):
        node = {"id": f"n{i}", "type": "if",
                "condition": {"type": "process_running", "processName": "Code"},
                "then": [node]}
    return node

cases = [
    ("unknown action type",
     lambda d: d["actions"].append({"id": "x", "type": "click_element", "selector": {}})),
    ("unknown field on a known action",
     lambda d: d["actions"][0].update({"recursive": True})),
    ("launch_process with both app and path",
     lambda d: d["actions"][3].update({"path": "C:\\Windows\\notepad.exe"})),
    ("launch_process with neither app nor path",
     lambda d: d["actions"][3].pop("app")),
    ("window selector with no matching fields",
     lambda d: d["actions"][3]["expect"].update({"selector": {}})),
    ("invalid key name in trigger",
     lambda d: d["trigger"].update({"keys": ["CTRL", "ALT", "PLUS"]})),
    ("missing schemaVersion",
     lambda d: d.pop("schemaVersion")),
    ("wrong schemaVersion",
     lambda d: d.update({"schemaVersion": 2})),
    ("variable name starting with a digit",
     lambda d: d["variables"].append({"name": "1bad", "type": "text"})),
    ("bad enum value for move_window position",
     lambda d: d["actions"].append({"id": "y", "type": "move_window",
                                    "selector": {"processName": "Code"},
                                    "position": "slightly_left"})),
    ("1 control-flow level + leaf (allowed)",
     lambda d: d["actions"].append(nest(1))),
    ("2 control-flow levels + leaf (allowed)",
     lambda d: d["actions"].append(nest(2))),
    ("3 control-flow levels (must be rejected)",
     lambda d: d["actions"].append(nest(3))),
    ("foreach inside foreach inside foreach (rejected)",
     lambda d: d["actions"].append({
         "id": "f0", "type": "foreach", "source": "candidates", "itemVariable": "selected",
         "body": [{"id": "f1", "type": "foreach", "source": "candidates", "itemVariable": "selected",
                   "body": [{"id": "f2", "type": "foreach", "source": "candidates",
                             "itemVariable": "selected",
                             "body": [{"id": "l", "type": "notify", "message": "x"}]}]}]})),
]

print("\nNEGATIVE — schema must reject these")
EXPECT_VALID = {"1 control-flow level + leaf (allowed)",
                "2 control-flow levels + leaf (allowed)"}
for label, fn in cases:
    doc = mutate(fn)
    errs = list(v.iter_errors(doc))
    rejected = bool(errs)
    want_rejected = label not in EXPECT_VALID
    good = rejected == want_rejected
    if not good:
        ok = False
    verb = "rejected" if rejected else "accepted"
    mark = "pass" if good else "FAIL"
    print(f"  {mark}  {label:44} -> {verb}")

# ---------- coverage: which primitives has anything actually exercised? ----------
# An action type no example uses is a shape nobody has validated. That matters most right
# before writing the C# records against it.
all_types = set()
for name, body in schema["$defs"].items():
    t = (body.get("properties") or {}).get("type")
    if isinstance(t, dict) and "const" in t and name not in ("Trigger",):
        # only action defs, not predicates/postconditions
        if name in [r["$ref"].split("/")[-1] for r in schema["$defs"]["LeafAction"]["oneOf"]] \
           or name in ("IfL0", "IfL1", "ForEachL0", "ForEachL1"):
            all_types.add(t["const"])

used = set()
def collect(node):
    if isinstance(node, dict):
        if isinstance(node.get("type"), str) and "id" in node or "type" in node:
            t = node.get("type")
            if isinstance(t, str) and t in all_types:
                used.add(t)
        for v in node.values():
            collect(v)
    elif isinstance(node, list):
        for v in node:
            collect(v)

for f in examples:
    collect(json.loads(f.read_text(encoding="utf-8")))

missing = sorted(all_types - used)
pct = 100 * len(used) / len(all_types)
print(f"COVERAGE — {len(used)}/{len(all_types)} action types exercised by examples ({pct:.0f}%)")
if missing:
    ok = False
    print("  FAIL — these action types are not exercised by any example, so their shape is")
    print("         unvalidated. Add one to examples/ before relying on it:")
    for m in missing:
        print(f"    {m}")

print()
if not ok:
    print("SOME CHECKS FAILED")
    sys.exit(1)
print("ALL CHECKS PASSED")

"""Pre-flight checks on the DSL schema, mirroring what the C# conformance tests will assert.

1. valid JSON
2. every internal $ref resolves
3. no $ref cycles  (the V2 structured-outputs precondition)
4. every object with "properties" also sets additionalProperties:false
5. no numeric/length constraints leaked into the schema (must live in the policy layer)
6. every property has a description
7. report the action type discriminators, for the C# record list
"""
import json, sys, re
from pathlib import Path

p = Path(sys.argv[1])
doc = json.loads(p.read_text(encoding="utf-8"))
fail = []
warn = []

# ---------- 2 & 3: refs resolve, and no cycles ----------
def resolve(ref):
    if not ref.startswith("#/"):
        return None
    node = doc
    for part in ref[2:].split("/"):
        part = part.replace("~1", "/").replace("~0", "~")
        if isinstance(node, dict) and part in node:
            node = node[part]
        else:
            return "MISSING"
    return node

def refs_in(node):
    out = []
    if isinstance(node, dict):
        for k, v in node.items():
            if k == "$ref" and isinstance(v, str):
                out.append(v)
            else:
                out += refs_in(v)
    elif isinstance(node, list):
        for v in node:
            out += refs_in(v)
    return out

for r in set(refs_in(doc)):
    if resolve(r) == "MISSING":
        fail.append(f"unresolvable $ref: {r}")

# cycle detection over the $defs graph
defs = doc.get("$defs", {})
edges = {}
for name, body in defs.items():
    targets = set()
    for r in refs_in(body):
        m = re.match(r"^#/\$defs/([^/]+)", r)
        if m:
            targets.add(m.group(1))
    edges[name] = targets

WHITE, GREY, BLACK = 0, 1, 2
colour = {n: WHITE for n in edges}
def visit(n, stack):
    colour[n] = GREY
    for t in edges.get(n, ()):
        if t not in colour:
            continue
        if colour[t] == GREY:
            fail.append("$ref CYCLE: " + " -> ".join(stack + [t]))
        elif colour[t] == WHITE:
            visit(t, stack + [t])
    colour[n] = BLACK
for n in list(edges):
    if colour[n] == WHITE:
        visit(n, [n])

# ---------- 4, 5, 6: structured-output subset hygiene ----------
BANNED = {"minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "multipleOf",
          "minLength", "maxLength", "minItems", "maxItems", "uniqueItems",
          "minProperties", "maxProperties", "patternProperties", "dependentSchemas",
          "if", "then", "else", "not"}

def walk(node, path):
    if isinstance(node, dict):
        # 4
        if node.get("type") == "object" and "properties" in node:
            if node.get("additionalProperties") is not False:
                fail.append(f"missing additionalProperties:false at {path}")
        # 5 -- only flag as schema keywords, not as property names
        if "properties" in node or "type" in node or "$ref" in node or "oneOf" in node:
            for b in BANNED & set(node.keys()):
                # 'if'/'then'/'else' are legitimate *property names* under a properties map,
                # handled because we only inspect schema-position nodes here.
                fail.append(f"unsupported keyword '{b}' at {path} (belongs in the policy layer)")
        # 6
        # Generic $ref targets carry no useful parameter description, so a property pointing at
        # one must supply its own or the generated docs read as boilerplate.
        GENERIC_REFS = {"TemplateString", "VariableName"}
        for pname, pbody in (node.get("properties") or {}).items():
            if not isinstance(pbody, dict):
                continue
            if "description" in pbody:
                continue
            if "const" in pbody:
                continue  # a const discriminator is self-describing
            ref = pbody.get("$ref", "")
            if ref:
                target = ref.split("/")[-1]
                if target in GENERIC_REFS:
                    fail.append(
                        f"{path}/properties/{pname} -> $defs/{target} needs its own description "
                        f"(the generic one is not a parameter description)")
                continue  # a specific $ref (WindowSelector, Condition, ...) describes itself
            warn.append(f"property without description: {path}/properties/{pname}")
        for k, v in node.items():
            if k == "properties":
                for pname, pbody in v.items():
                    walk(pbody, f"{path}/properties/{pname}")
            elif k in ("$defs", "definitions"):
                for dname, dbody in v.items():
                    walk(dbody, f"{path}/$defs/{dname}")
            elif k in ("oneOf", "anyOf", "allOf", "prefixItems"):
                for i, s in enumerate(v):
                    walk(s, f"{path}/{k}/{i}")
            elif k == "items":
                walk(v, f"{path}/items")
    elif isinstance(node, list):
        for i, v in enumerate(node):
            walk(v, f"{path}/{i}")

walk(doc, "#")

# ---------- 7: action discriminators (the C# record list) ----------
# Only ACTION defs count: leaf actions plus the control-flow wrappers. Trigger and the
# predicate/postcondition unions also carry a 'type' const but are not actions.
leaf_defs = [r.split("/")[-1] for r in refs_in(defs.get("LeafAction", {}))]
control_defs = ["IfL0", "IfL1", "ForEachL0", "ForEachL1"]

actions = {}
for name in leaf_defs + control_defs:
    t = (defs.get(name, {}).get("properties") or {}).get("type")
    if isinstance(t, dict) and "const" in t:
        actions.setdefault(t["const"], []).append(name)

print(f"parsed OK: {p.name} ({p.stat().st_size:,} bytes)")
print(f"$defs: {len(defs)}   distinct $refs: {len(set(refs_in(doc)))}")
print(f"LeafAction branches: {len(leaf_defs)}")
print(f"\naction discriminators ({len(actions)} distinct -> that many C# records):")
for const in sorted(actions):
    names = actions[const]
    note = "  (depth levels share one CLR type; depth enforced by policy)" if len(names) > 1 else ""
    print(f"  {const:24} <- {', '.join(names)}{note}")

if warn:
    print(f"\nWARNINGS ({len(warn)}):")
    for w in warn[:40]:
        print("  " + w)
if fail:
    print(f"\nFAILURES ({len(fail)}):")
    for f in fail:
        print("  " + f)
    sys.exit(1)
print("\nALL CHECKS PASSED")

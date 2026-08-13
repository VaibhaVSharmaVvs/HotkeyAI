# Schema checks

Two Python checks over `schema/hotkeyai-dsl-v1.schema.json`. They exist because the schema is
the source of truth and needs guarding *before* there is any C# to guard it with — and because
some of what they assert is cheaper to express here than in a unit test.

```powershell
pip install jsonschema
python tools/schema-checks/check_schema.py schema/hotkeyai-dsl-v1.schema.json
python tools/schema-checks/validate_examples.py .
```

## `check_schema.py` — schema hygiene

| Check | Why it matters |
|---|---|
| Valid JSON, all internal `$ref`s resolve | Obvious, but easy to break while hand-editing 1000 lines |
| **No `$ref` cycles** | The V2 precondition. Structured outputs reject recursive schemas, so a cycle introduced here would surface as a hard blocker in V2 — after the schema was already baked into stored automations |
| Every `type: object` with `properties` sets `additionalProperties: false` | Required by structured outputs, and it is also the allowlist rule: unknown field means reject |
| No unsupported keywords (`minimum`, `maxLength`, `if`/`then`, `not`, …) | These are outside the structured-output subset. Anything that can't be expressed here belongs in the policy validator, and stating a bound in a `description` is how the planner still learns about it |
| Every property has a `description` (`const` and `$ref` exempted) | Descriptions are the planner's prompt material, not comments. A property with no description is a primitive the planner will misuse |
| Reports the action discriminators | This is the C# record list. Currently 25 |

## `validate_examples.py` — does the contract actually work

Validates every file in `examples/`, then mutates a known-good plan in ways a planner
plausibly gets wrong and asserts each one is rejected: unknown action type, unknown field,
`launch_process` with both or neither of `app`/`path`, an empty window selector, a bad key
name, a bad enum value, a malformed variable name, and nesting past the cap in both `if` and
`foreach`.

The negative cases are the point. A schema that accepts everything is worthless as a
generation constraint, and only the rejections prove it constrains anything.

## Relationship to the C# tests

These do not go away when `HotkeyAI.Core.Tests` exists — they cover the schema *as a
document*, which is a different question from whether the C# records agree with it. Planned
split:

- **here** — schema hygiene, structured-output-subset compliance, no `$ref` cycles, examples validate
- **C#** — bidirectional record/schema conformance, the policy layer (numeric bounds, allowed
  roots, variable dataflow), and which *layer* rejects a given bad plan

Both run in CI.

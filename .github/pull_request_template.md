## What

<!-- What changes, in one or two sentences. -->

## Why

<!-- The problem this solves. Link an issue if there is one. -->

## Checklist

- [ ] `dotnet test Bpmn.slnx` passes
- [ ] No host-specific references introduced (the CI guard and `HostAgnosticBoundaryTests` enforce this)
- [ ] Public API changes are reflected in `docs/`
- [ ] A behavior change to the interpreter or the reader/writer has a test that fails without it
- [ ] If the on-disk format changed, the JSON Schema and its version were updated together

## BPMN conformance

<!-- If this touches the reader or writer, say what happened to the MIWG corpus results:
     unchanged, newly passing cases, or newly skipped ones with reasons. -->

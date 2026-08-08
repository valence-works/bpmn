# Decision records

Architecture decisions for this library, with the reasoning that produced them.

These exist because the BPMN implementation this library was extracted from had its rationale
recorded nowhere except in commit messages and slice specifications that made sense only inside that
codebase. When the code moved, the reasoning would have been lost. Writing it down in BPMN language,
readable by someone who has never seen the original, was a precondition for the extraction.

They are short and they take positions. An ADR that hedges is not worth keeping.

| ADR | Decision |
| --- | --- |
| [0001](0001-bpmn-semantics-ship-as-a-host-agnostic-library.md) | BPMN semantics ship as a host-agnostic library |
| [0002](0002-the-host-port-is-synchronous-and-command-returning.md) | The host port is synchronous and command-returning |
| [0003](0003-faults-are-a-verdict-not-a-command.md) | Faults are a verdict, not a command |
| [0004](0004-capabilities-refuse-at-graph-build-time.md) | Missing host capabilities refuse at graph-build time |
| [0005](0005-the-library-owns-a-versioned-payload-format.md) | The library owns a versioned payload format |
| [0006](0006-vendor-extensions-are-retained-as-typed-data.md) | Vendor extensions are retained as typed data |

## Writing a new one

Number sequentially. Include Status, Context, Decision, Consequences, and Alternatives considered.

State the alternatives honestly, including the ones that were nearly right — a rejected option with a
real argument behind it is the most useful part of the record, because it is what stops the same
question being reopened every six months.

Consequences should include the costs. An ADR that lists only benefits has not finished thinking.

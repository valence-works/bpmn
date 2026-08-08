# BPMN MIWG reference models

The `.bpmn` files in this directory are the reference models of the **BPMN Model Interchange Test
Suite**, created by the **BPMN Model Interchange Working Group (BPMN MIWG)** at the **Object
Management Group (OMG)**.

- Source: https://github.com/bpmn-miwg/bpmn-miwg-test-suite (`Reference/`)
- Working group: http://www.omgwiki.org/bpmn-miwg
- License: **Creative Commons Attribution 3.0 Unported (CC BY 3.0)** —
  https://creativecommons.org/licenses/by/3.0/

The files are redistributed here unmodified, under the terms of that license, with attribution to the
BPMN MIWG as their author. They are used as an external conformance corpus for this library's reader
and writer.

GitHub reports this suite's license as `NOASSERTION` because its Creative Commons license is not one
of the code licenses GitHub detects. The upstream `LICENSE.txt` states CC BY 3.0 explicitly, which
permits redistribution with attribution. This notice is that attribution.

## Why these files and not the whole suite

Only the contents of the suite's `Reference/` directory are vendored, and only the `.bpmn`
serializations. The suite also contains exports produced by roughly thirty commercial and open-source
modeling tools, plus PDF, PNG, and Visio renderings of each model. Those are valuable for comparing
tools against each other, which is the working group's purpose, but they are not what this library
needs: the reference models are the canonical statement of what each test case *is*.

## What a failure here means

These models were designed by a standards working group to expose interchange defects, so they will
find real gaps. A failure in this corpus is not automatically a regression in this library — the model
may exercise a construct that is not implemented yet.

Unsupported cases are skipped with a stated reason rather than silently passing, and the current
coverage is recorded alongside the tests. Read a red result here as a finding to triage, not a build
break to suppress.

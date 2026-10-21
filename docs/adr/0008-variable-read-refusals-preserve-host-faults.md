# Variable read refusals preserve host faults

- Status: Accepted
- Date: 2026-10-17

## Context

A host can know a variable exists and refuse to disclose it. Reporting that read as absent or null
silently turns a collection-mode multi-instance activity into an empty loop. Reporting it as stored
externally loses the host's reason behind a generic inline-storage fault. Neither answer expresses
an intentional refusal.

## Decision

Append `Refused` to `BpmnValuePresence`. A refused `BpmnValue` carries optional `faultCode` and
`faultMessage` metadata, and the `BpmnValue.Refused(code, message)` factory constructs a validated
refusal. The reader returns `true`: it answered the read, but withheld the value. No inline value is
required or exposed.

Collection-mode multi-instance evaluation checks refusal before inline-value resolution, in both
sequential and parallel modes. It returns a fault verdict with the host's exact code and message,
without creating iterations. A malformed refusal lacking a usable code falls back to the existing
collection-unreadable code; missing message metadata gets a deterministic diagnostic. It never
becomes an empty loop or an external-storage fault. The interpreter remains synchronous and pure.
Empty or whitespace-only host messages remain unchanged on the fault verdict. The diagnostic uses
fallback text because diagnostic records require a nonblank message.

Keep the existing three-argument constructor and deconstructor. New metadata is declared as
explicitly named, nullable init properties and omitted from JSON when null, so existing value JSON
is unchanged. Existing enum numbers remain unchanged. `BpmnValue` is host-read data and is not
reachable from the current definition/state payload schema roots, so that schema and its format
version do not change. This is an additive host-port API for the 0.4.0 package line.

The reference variable reader provides `SetRefused` so a host can exercise this outcome without a
custom adapter. Hosts own the diagnostic text and should supply only information suitable for the
recipient of the fault verdict.

## Consequences

Hosts distinguish unknown, absent, null, inline, external and refused reads. Existing readers and
serialized values continue to work. Hosts can retain their own fault taxonomy without embedding a
particular host policy in the library. Clients interpreting the presence enum must recognize the
new value; older clients cannot represent a refusal.

## Alternatives considered

- Throw from the reader: loses a deterministic fault verdict and mixes host exceptions into the
  interpreter's return protocol.
- Reuse absent or external storage: retains the silent empty loop or misleading fault this change
  addresses.
- Add a host-specific reader or secret policy: violates the host-neutral boundary; the generic
  refusal is sufficient.

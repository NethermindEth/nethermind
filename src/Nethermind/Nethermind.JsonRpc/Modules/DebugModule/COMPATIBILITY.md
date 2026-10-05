# `debug_traceCall` compatibility

The reference for this compatibility work is official Geth commit
`23408c2b17c3f4e094921d2ff305bfef018b1ace`.

## JavaScript diagnostics

Nethermind executes custom JavaScript tracers in V8; Geth uses Goja. Compatibility
requires matching error codes, underlying causes, callback behavior and trace
results, but not identical engine-specific diagnostic text.

Goja may append an evaluation location and bytecode instruction offset, such as
`at <eval>:1:89(13)`. V8 has different execution internals; these offsets are not
synthesized. Timeout locations can also vary between repeated Geth executions.
A request deadline is reported as `-32000` / `execution timeout`; engine-specific
location and callback annotations may differ. Ordinary callback failures identify
the failing callback without attempting to reproduce Goja's stack rendering.

User-script failures must not be confused with unexpected host failures, fatal
engine failures or transport cancellation. Invalid global `slice` bounds interrupt
the tracer even inside JavaScript `try/catch`, matching Geth; `toHex(null)` raises
a catchable type error.

## Transaction execution deadlines

`debug_traceTransaction` applies Geth's five-second execution deadline when
`timeout` is omitted, including opcode and native tracers. This changes the
previous native/opcode behavior, which was limited only by `JsonRpc.Timeout`.
Set the request's `timeout` to a Go duration such as `"30s"` for a larger execution
budget; increasing `JsonRpc.Timeout` alone does not increase this default.
The server's `JsonRpc.Timeout` remains an independent outer limit.

The execution timer starts after tracer setup and preceding-transaction replay,
when the selected transaction starts. `noopTracer` ignores the execution stop,
matching Geth. An execution timeout is not a successful complete trace.
The precommit and committed-streaming response rules below apply to both
`debug_traceCall` and `debug_traceTransaction`.

## Streaming deadlines

Before a response is committed, or when HTTP response buffering allows replacement,
a tracing deadline produces a normal top-level JSON-RPC error with code `-32000`
and message `execution timeout`, without a successful `result`.

After a streaming response is committed, Nethermind closes the partial trace as
valid JSON and includes `failed: true`, `error: "execution timeout"`, and
`errorCode: -32000` in the result. Captured entries are incomplete. This deliberate
streaming behavior differs from Geth's top-level error and is still recorded as a
failed request, not successful execution.

## Buffered opcode byte budgets

For a positive `limit`, buffered opcode traces use a conservative size estimate
from fixed overhead, string lengths, stack/memory word counts, and a storage-update
counter. They can stop collecting earlier than Geth or Nethermind's streamed
traces, which count actual serialized bytes. Repeated storage accesses and small
stack values can increase the difference. This is a deliberate performance and
simplicity trade-off, not exact byte-boundary parity.

Zero or null remains unlimited; negative limits suppress opcode logs. Execution
continues after capture stops, preserving final gas, failure status, and output.
Named tracers are unaffected. Approximate truncation is not an allowed mismatch
in the 304-request acceptance runner; boundary coverage is maintained separately.

## Verification boundaries

Compatibility is established by individual source checks, regression tests and
same-block live comparisons; a passing selected corpus is not proof for every
possible request. These diagnostic and streaming exceptions do not permit gas,
state, callback-order or trace-result differences.

## Bare JavaScript string results

Nethermind deliberately returns the actual string value from a custom tracer's
`result()` callback rather than reproducing a pinned Geth serialization bug.
Geth directly serializes Goja's internal root value, so equal JavaScript strings
can produce different JSON depending on their internal representation:

- `return toHex(new Uint8Array(8));` produces `{}` in the pinned Geth.
- Adding `+ ""` to the same expression produces `"0x0000000000000000"`.
- A bare Unicode literal such as `"é"` can produce `[65279,233]`.

Nethermind returns the actual string in each case. This is an intentional result
compatibility exception, separate from diagnostic-text differences; it is not
reported as an exact-match pass. Returning an object or array, for example
`return { value: toHex(new Uint8Array(8)) };`, avoids the observed Geth bug.
Wrapped results remain subject to normal compatibility checks. Built-in native
tracers and opcode traces are not affected by this exception.

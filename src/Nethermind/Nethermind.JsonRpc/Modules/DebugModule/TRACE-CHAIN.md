# Native chain tracing subscriptions

Available over WebSocket and IPC when the Debug module is enabled:

```json
{"jsonrpc":"2.0","id":1,"method":"debug_subscribe","params":["traceChain","0x100","0x102",{"tracer":"callTracer","timeout":"5s"}]}
```

The result is a subscription ID. Replay begins only after the complete response has been written, including the enclosing batch when applicable. There is no direct `debug_traceChain` method and no HTTP subscription support.

The finite range is `(start, end]`. Notifications use `debug_subscription` with `params.subscription` and `params.result`, whose fields are `block`, `hash`, and transaction-ordered `traces`. Intermediate empty blocks are omitted; the terminal block is emitted even when empty. Blocks are fetched by number during replay, so this is not an ancestry snapshot across a reorganization.

A successful transaction entry contains `txHash` and `result`. A tracer failure preserves earlier entries, emits `txHash` and `error` for the failing transaction, and leaves the remaining entries null. Tracer construction precedes duration parsing. Invalid duration strings therefore produce transaction errors after acknowledgement, rather than invalid-parameter responses during registration. Call-only override and transaction-index options do not apply.

Each transaction uses the requested Go-style duration, defaulting to five seconds. `JsonRpc.Timeout` does not impose a separate block deadline. Subscription cancellation also interrupts JavaScript construction/setup; the transaction deadline itself starts after construction.

## Resource and termination behavior

- Replay requires retained parent state; this endpoint does not regenerate pruned state.
- Subscriptions use the existing bounded exclusive Debug pool and its queue limits. A rental covers one block's replay, notification and result disposal, and is returned between blocks so queued Debug work can proceed. A slow block can still delay ordinary requests. Admitted subscription rentals wait until acquisition or cancellation, without the ordinary request rental timeout. Queue admission limits remain enforced; enabling Debug on an untrusted endpoint permits expensive work.
- Notifications are awaited, not accumulated in an unbounded producer queue. A slow recipient applies backpressure.
- Use `debug_unsubscribe` with the returned ID to cancel replay or a pending rental. Closing the connection also cancels its subscriptions.
- Natural completion releases replay resources but retains the lightweight subscription registration until unsubscribe/disconnect. Consequently, unsubscribe after the terminal notification returns `true`, matching Geth. Long-lived clients should unsubscribe completed subscriptions.
- There is no additional terminal-error notification. Missing blocks/state or queue admission failures after acknowledgement terminate production and are logged. Clients must not treat a missing terminal block notification as successful completion.

This describes the native transport/lifecycle contract, not a claim that every existing tracer output or invalid-request diagnostic is identical to Geth. In particular, generic HTTP and invalid-selector error wording may differ.

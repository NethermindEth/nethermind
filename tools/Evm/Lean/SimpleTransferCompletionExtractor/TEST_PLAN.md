# Test plan

The test project covers:

1. exact production source/dependency identities, error-free semantic closure, and standard DI reachability;
2. all ordered stages/effects and required adapter bindings;
3. malformed/tampered IR and manifest rejection;
4. public source-drift rejection;
5. unsupported syntax fail-closed behavior;
6. paired admission mutations: guard polarity, `PayValue` order, schedule/policy, access-list
   overload/order/deduplication, max/effective-block, counter gating, receipt-root request,
   transfer-log topic order, exact-build-up equality, receiver, argument order, and no-frame CFG;
7. model-boundary mutations for the generated address-hash projection and derived transfer-log topics/data,
   extensional access-set interpretation (including reordered carriers versus reordered events),
   status/receipt projection, opaque world-root inputs, and the explicit normal-return domain;
8. absence of generated `theorem`, `axiom`, `sorry`, and `admit` escapes; and
9. presence of a concrete receipt-continuation input and independent reference/refinement modules.

These tests establish source admission and generated/reference model agreement (with extensional
access carriers and ordered events). They do not turn
the handwritten formulas into a production-body or live-adapter proof. The test executable
deliberately does not run in the editing lane. `Verify.ps1` is the serialized build-lane entry
point and must be used by the parent agent when .NET/Lake/Lean execution is authorized.

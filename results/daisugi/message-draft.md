Hey! We went through AA-PQ to see how to get Daisugi running on upstream Nethermind with no patches.

Short version: Daisugi is pinned to an old snapshot of our EIP-8288 branch (`4a17b571`, early Oct). The branch has moved a lot since then (deps hash, proof moved from header to body, new VK/guest), so it can't validate the current chain history as-is.

Our proposal: restart Daisugi from a fresh genesis on our current `feature/eip8288-lean`. No overlays, no `daisugiLegacyFrames`, one protocol version. On our side we'd fix the remaining gaps (EIP-8141 activation getting rescheduled by 8288, verify-gas config, RPC shapes your wallet relies on).

A few questions:
1. Is it OK to drop the current chain history and redeploy the accounts/factory? The `ACTIVATION.md` says "do not reset genesis", so I want to check that's not a hard requirement.
2. Is the old scalar-nonce frame wallet still in use, or only the aggregated one (keyed nonce `[0]`)?
3. Is adaptive aggregation still planned? The repo says it's not active.
4. On the CL side, the Engine payload field changed too (`starkProof` in the payload body), so the Lighthouse patch would need an update. Who owns that?

Happy to jump on a call if that's easier.

# MAX_VERIFY_GAS campaign: tasks

Updated 2026-10-06. Measurements closed. See `plan.md` (2026-10-05) for context.

## Done
- [x] v5 measurement set on master `d804d33b`: Mid, Low, Builder (capped 3.8 GHz), 4 suites × 2 runs; §4a validation passes everywhere
- [x] #14265 arm (`3a40baed`, PR at `40fb86d6`) + 2 paired runs on Low: verdict reduced
- [x] Harness fixes for #14205 signer cache (spam as decoded copy, victim cache cleared) and Lukasz's review, pushed to #14135
- [x] #14265: Claude review findings fixed, pre-pass added and measured, ready for review
- [x] Vault: `01. Results` v5, `03. Deliverables` §1 Notion v5 draft (decision table), §2b Slack final draft, pre-registration with every amendment
- [x] Artifact v5 published, same URL (version 15)

## Manuel
- [ ] Publish Notion from `03. Deliverables` §1, then add its link to Slack 2b
- [ ] Send Slack 2b
- [ ] Ask Daniil / Stefano: tier for the build path, B, k; whether this goes to EF / EIP authors (they write the EF note from Notion + artifact)

## Waiting on others
- [ ] #14135 re-approval (Lukasz, Daniil)
- [ ] #14265 review

## Later (not blocking)
- [ ] Per-head signature budget (follow-up to #14265)
- [ ] Fair per-head budget design (with Daniil)
- [ ] Clean up test branches: `test/frame-tx-sig-preempt-measure`, `-control`, `test/frame-tx-v5-sig`, local `bisect-gc`
- [ ] Optional: uncapped Builder window, if the EXPB runner owners allow one

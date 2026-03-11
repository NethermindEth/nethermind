---
name: eip-implementation-reviewer
description: Use when reviewing an EIP implementation for spec compliance, missing tests, or EIP interaction gaps. Complements the generic review skill — run both, not one or the other.
allowed-tools: [Bash(git diff*), Bash(git merge-base*), Bash(git log*), Bash(git status*), Read, Grep, Glob, WebFetch]
---

# EIP Reviewer

Cross-references implementation diff against EIP spec text. **Only report findings with >80% confidence.**

## Progress tracker

```
EIP-XXXX Review:
- [ ] Step 1: Fetch EIP spec + prerequisites
- [ ] Step 2: Map diff to spec requirements
- [ ] Step 3: Parallel checks (A: fidelity, B: tests, C: interactions)
- [ ] Step 4: Score + report
```

## Step 1 — Fetch EIP Spec

```
Primary:  https://raw.githubusercontent.com/ethereum/EIPs/refs/heads/master/EIPS/eip-{number}.md
Fallback: https://eips.ethereum.org/EIPS/eip-{number}
```

Fetch all EIPs listed in the `Requires` header — specs are deltas. Extract as bullet points (quote directly when referencing later):

- Numeric constants, pseudocode/formulas, conditional logic, revert conditions
- Backward compatibility and security considerations

Classify each as **MUST** (violation = finding) or **SHOULD** (advisory only). Warn if EIP is Draft/Review status.

## Step 2 — Map Diff to Spec

If diff not provided: `git diff $(git merge-base HEAD origin/master) HEAD`

Check for prior work: `git log --all --oneline --grep="IsEip{number}"` — diff may be part 2 of N.

Map each changed code section to a spec requirement. Flag:

- **Unmapped code** — no spec requirement (over-implementation?)
- **Unmapped spec** — no code change (gap? check prior PRs)

Triage if >30 files: prioritize `Nethermind.Evm/`, `Nethermind.Specs/`, `Nethermind.Core/Specs/`, transaction processing.

## Step 3 — Parallel Spec Compliance Checks

Launch up to 4 sub-checks with the EIP spec text and diff.

### A: Spec Fidelity

- **Constants**: Named constants with exact spec values — no magic numbers
- **Formulas**: Implementation matches spec pseudocode. Watch: off-by-one in `ceil()`, `long` truncation of `UInt256`, rounding direction. When the spec describes the same value in multiple places (parameter table + pseudocode), verify they are the same charge before reporting as missing — cross-reference the parameter table breakdown against pseudocode line items
- **Conditionals**: Every "MUST revert if..." has a branch. Missing revert = CRITICAL

### B: Tests + Backward Compat

- **Flag gating**: New behavior behind `IsEip{number}Enabled`. Flag-off path unchanged. No re-gating prerequisites
- **Test checklist**:
  - For each MUST in the spec (including backward compat / security sections), verify a corresponding test exists — at the appropriate layer (unit, integration, or RPC)
  - flag-off test (`OverridableReleaseSpec`), boundary tests (exact gas / off-by-1), revert tests per "MUST revert"
  - For gaps: suggest tests using project patterns — `VirtualMachineTestsBase` for EVM opcodes/gas, `BlockchainTestBase` for block-level behavior (withdrawals, tx types, state changes), `OverridableReleaseSpec` for flag toggling, `Prepare.EvmCode` for bytecode construction

### C: Interactions + Pipeline

- **Flag pipeline**: All files updated per `implementation-patterns.md` Layer 1-5 (IReleaseSpec → ReleaseSpec → Decorator → OverridableReleaseSpec → fork → ChainSpec\*). Missing = breaks non-mainnet
- **Siblings**: Other EIPs in same fork sharing code paths. Compound conditions with new flag
- **Gas composition**: If gas-related, check that the new gas logic composes correctly with existing gas-related EIPs already active in the target fork

See `../references/eip/common-pitfalls.md` for general patterns.

### D: Pattern Completeness (mandatory — must appear in report)

This check is **not optional**. You must include a pattern checklist table in your report.

1. Read `../references/eip/implementation-patterns.md`
2. Identify which patterns apply to this EIP (new opcode, new header field, new tx type, etc.)
3. For each applicable pattern, list **every numbered item** and whether the **diff modifies** that file. A pre-existing file that is not modified by the diff counts as MISSING — the EIP's changes need to reach it:

```
Pattern: "New header field" (items 1-12)
 1. BlockHeader.cs — PRESENT in diff
 2. Block.cs — PRESENT in diff
 3. HeaderDecoder.cs — PRESENT in diff
 ...
 8. EngineRpcModule.{ForkName}.cs — MISSING ← finding
 9. IEngineRpcModule.{ForkName}.cs — MISSING ← finding
```

## Step 4 — Score + Report

Score each candidate finding (0-100):

| Score | Meaning                                           |
| ----- | ------------------------------------------------- |
| 0     | False positive, pre-existing, or unmodified lines |
| 50    | Real but unlikely in practice                     |
| 75    | Likely real, violates MUST                        |
| 100   | Confirmed with concrete proof                     |

**Discard below 80.** Do NOT report: pre-existing issues, unmodified lines, compiler/CI-catchable issues, intentional EIP changes, SHOULD non-compliance.

### Finding format

```
### F-{N}: {Title}
Confidence: {score}/100
Severity: CRITICAL | HIGH | MEDIUM | LOW
Category: Spec Divergence | Missing Test | EIP Interaction | Backward Compat | Pipeline Gap
Spec Reference: "{quoted spec text}"
File: {path}:{line}
Problem: {one sentence}
Impact: {what goes wrong}
Verification: {concrete test or benchmark}
```

**Severity:** CRITICAL = invalid blocks/state corruption, HIGH = triggerable spec violation, MEDIUM = missing test for mandated behavior, LOW = non-consensus deviation.

**Verdict:** CRITICAL/HIGH → **BLOCK**, MEDIUM only → **APPROVE WITH CONDITIONS**, LOW/none → **APPROVE**.

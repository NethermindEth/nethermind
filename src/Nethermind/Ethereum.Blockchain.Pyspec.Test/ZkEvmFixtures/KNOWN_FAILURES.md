# Known failing zkEVM tests (Amsterdam)

Baseline `dotnet test` run against `tests-zkevm@v0.4.0` fixtures (21 491 cases) under
`ONLY_ZKEVM=1` on macOS-arm64, **before any witness comparison logic was added**:

- **Total:** 21 492 (one extra discovery probe)
- **Passed:** 21 471 (99.9 %)
- **Failed:** 21 (0.1 %)
- **Skipped:** 0
- **Duration:** 2 m 07 s

All 21 failures are **EIP-7702-related** (the SetCode transaction type and authorization
list semantics introduced in Prague). They cluster into three sub-areas:

## Failure groups

### Group A — EIP-7702 SetCode tx + downstream feature interaction (13 cases)

The fixture issues a `tx_type=4` (SetCode) transaction that also touches another feature
(blobhash, beacon-root contract, BAL, auth signature edge cases). Nethermind rejects or
mis-executes the transaction.

```
test_bal_7702_null_address_delegation_no_code_change[fork_Amsterdam-blockchain_test]
test_blobhash_gas_cost[fork_Amsterdam-tx_type_4-blockchain_test_from_state_test-blobhash_index_0]
test_blobhash_gas_cost[fork_Amsterdam-tx_type_4-blockchain_test_from_state_test-blobhash_index_1]
test_blobhash_gas_cost[fork_Amsterdam-tx_type_4-blockchain_test_from_state_test-blobhash_index_2]
test_blobhash_gas_cost[fork_Amsterdam-tx_type_4-blockchain_test_from_state_test-blobhash_index_3]
test_blobhash_gas_cost[fork_Amsterdam-tx_type_4-blockchain_test_from_state_test-blobhash_index_4]
test_blobhash_gas_cost[fork_Amsterdam-tx_type_4-blockchain_test_from_state_test-blobhash_index_115792089237316195423570985008687907853269984665640564039457584007913129639935]
test_blobhash_gas_cost[fork_Amsterdam-tx_type_4-blockchain_test_from_state_test-blobhash_index_72901072107898194510616918724280211781393090952923809435170590639787343028527]
test_blobhash_opcode_contexts_tx_types[fork_Amsterdam-tx_type_4-blockchain_test_from_state_test]
test_tx_to_beacon_root_contract[fork_Amsterdam-tx_type_4-blockchain_test-call_beacon_root_contract_True-auto_access_list_False]
test_tx_to_beacon_root_contract[fork_Amsterdam-tx_type_4-blockchain_test-call_beacon_root_contract_True-auto_access_list_True]
test_valid_tx_invalid_auth_signature[fork_Amsterdam-blockchain_test_from_state_test-s=SECP256K1N_OVER_2-1]@bigmem
test_valid_tx_invalid_auth_signature[fork_Amsterdam-blockchain_test_from_state_test-s=SECP256K1N_OVER_2]@bigmem
```

### Group B — Delegation clearing on undelegated account (5 cases)

When an EOA that *isn't currently delegated* receives an authorization with `address = 0x00…`
("clear delegation"), Nethermind doesn't apply the semantics the fixture expects.

```
test_delegation_clearing[fork_Amsterdam-blockchain_test_from_state_test-undelegated_account-not_self_sponsored]
test_delegation_clearing[fork_Amsterdam-blockchain_test_from_state_test-undelegated_account-self_sponsored]
test_delegation_clearing_and_set[fork_Amsterdam-blockchain_test_from_state_test-undelegated_account]
test_delegation_clearing_tx_to[fork_Amsterdam-blockchain_test_from_state_test-undelegated_account-not_self_sponsored]
test_delegation_clearing_tx_to[fork_Amsterdam-blockchain_test_from_state_test-undelegated_account-self_sponsored]
```

### Group C — Double authorization with RESET (3 cases, `@bigmem`)

A transaction with two authorizations, where the *first* is a RESET (clear). Nethermind's
ordering or state-of-account-at-each-step seems to disagree with EELS.

```
test_double_auth[fork_Amsterdam-blockchain_test_from_state_test-second_delegation_DelegationTo.CONTRACT_A-first_delegation_DelegationTo.RESET]@bigmem
test_double_auth[fork_Amsterdam-blockchain_test_from_state_test-second_delegation_DelegationTo.CONTRACT_B-first_delegation_DelegationTo.RESET]@bigmem
test_double_auth[fork_Amsterdam-blockchain_test_from_state_test-second_delegation_DelegationTo.RESET-first_delegation_DelegationTo.RESET]@bigmem
```

## Observed exception types

These map onto the four error-message shapes visible in the test output:

| Exception | Root cause |
|---|---|
| `Withdrawals root hash mismatch ... expected 0x0101…0101` | seen on some EIP-7702 tests with a fixture-specific marker — likely an EELS test convention rather than a Nethermind bug |
| `insufficient sender balance for gas * price + value` | EIP-7702 sender that should have been delegated/cleared is being charged differently |
| `sender has deployed code` (EIP-3607) | Nethermind's EIP-3607 guard fires on EOAs with EIP-7702 delegation code; should be carved out |
| `Block gas limit exceeded ... fails EIP-8037 inclusion check` | EIP-7702 tx not accounted for correctly in the new EIP-8037 dimension split |

## Notes for follow-up

- All 21 failures predate witness-comparison logic. The witness assertion only fires
  **after** a successful block import, so these cases never reach it.
- The cluster around EIP-7702 suggests there's a single missing piece (e.g. delegation
  state lookup, EIP-3607 carve-out) that, once fixed, may resolve most/all of them.
- The simplest fix to attempt first is the **EIP-3607 / EIP-7702 carve-out** in transaction
  validation: an EOA with delegation code (`0xef0100‖address`) should still be a valid tx
  sender. Currently Nethermind throws `sender has deployed code`.

## How to reproduce locally

```bash
ONLY_ZKEVM=1 dotnet test \
  --project src/Nethermind/Ethereum.Blockchain.Pyspec.Test/Ethereum.Blockchain.Pyspec.Test.csproj \
  -c release --no-build 2>&1 | tee /tmp/zkevm-baseline.log

grep -E 'failed Test\(' /tmp/zkevm-baseline.log \
  | sed -E 's/.*failed Test\(([^)]+)\).*/\1/' | sort -u
```

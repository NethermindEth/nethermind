# EEZ stateless fixtures

Blocks, execution witnesses and chain configurations recorded from EEZ deployments, copied unchanged from
`eez-association/eez-rollup0` at `9b9491c45875fdaf45d06ad0f092691a49a85936`
(`crates/eez-prover-stateless/tests/fixtures`), licensed `MIT OR Apache-2.0`.

| Directory | Content | Expected values |
| --- | --- | --- |
| `stateless-block-13` | empty block 13, chain id 1 | hash, post-state root |
| `stateless-checkpoint-2175` | block 2175, three transactions, chain id 1 | per-transaction state roots, block hash |
| `nonzero-outbound-630` | window 626..630, chain id 1 | final post-state root |
| `captured-devnet-window-84` | window 79..84, EEZ genesis chain id 6290 | window pre and post block hashes |

The chain id 1 recordings predate the native system transaction type: their system calls are ordinary signed
transactions.

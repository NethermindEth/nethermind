# Account-read opcode extractor

This standalone Roslyn tool admits the selected standard-mainnet Nethermind sources for `BALANCE`, `EXTCODESIZE`, `EXTCODECOPY`, and `EXTCODEHASH`, emits deterministic semantic IR, round-trips and validates that IR, then generates theorem-free Lean consumed by an independent refinement module.

The extractor and its tests are included in `Evm.slnx`; see [SCOPE.md](SCOPE.md) for the exact claim and remaining assumptions.

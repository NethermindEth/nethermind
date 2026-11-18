# Stateless Nethermind on OpenVM RV32

The guest of [OpenVM](../Nethermind.Stateless.OpenVMGuest/README.md) built for
OpenVM's RV32 line (`main`), which is what OpenVM proves today. It takes the
same inputs and publishes the same Keccak-256 digest of its result.

See the [shared guest guide](../Nethermind.Stateless.Guest/README.md) for
build and standard-block execution commands. `make run` executes the guest on
the RV32 runner of NethermindEth/bflat-openvm, which does not generate or
verify proofs. The guest needs the standard VM configuration of the OpenVM SDK
(`SdkVmConfig::standard()`), so the ELF is all that a host needs.

Use `make run INPUT=<file> OPENVM_ARGS=--execute-only` to skip proving-key
setup and cost metering, as correctness CI does. Omit `OPENVM_ARGS` to report
instruction counts and trace costs.

Unlike the RV64 guest it has no division hint, which only the RV64 runner
offers, and no MUL on the bigint extension (`mul.S` is RV64 code).

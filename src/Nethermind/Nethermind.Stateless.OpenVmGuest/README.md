# Stateless Nethermind on OpenVM

See the [shared guest guide](../Nethermind.Stateless.Guest.Shared/README.md) for
build and standard-block execution commands, the expected public-output digest,
and GPU proving limitations.

`make run` executes the guest; the pinned runner does not generate or verify
cryptographic proofs. Keep the guest's `openvm.toml` with the ELF when running it.

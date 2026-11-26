# Stateless Nethermind on OpenVM

See the [shared guest guide](../Nethermind.Stateless.Guest/README.md) for
build and standard-block execution commands, the expected public-output digest,
and GPU proving limitations.

`make run` executes the guest; the pinned runner does not generate or verify
cryptographic proofs. The guest needs the standard VM configuration of the
OpenVM SDK (`SdkVmConfig::standard()`), which ere also uses, so the ELF is all
that a host needs.

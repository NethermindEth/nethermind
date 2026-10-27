# Stateless Nethermind on Airbender

The guest targets ZKsync Airbender v3: RV32IM, a 4 MiB ROM, and a machine that
proves only the unsigned RV32M instructions. It publishes the Keccak-256 of the
validation result, as Airbender's public output is the eight words in x10-x17.
The full result is printed on the UART.

## Toolchain

No published bflat image carries the riscv32 compiler yet, so `make build` takes
three local builds:

- `BFLAT_DIR`: the output directory of a bflat build with the Airbender layout,
  that is, bflat built with `LocalCompilerDir` (a riscv32 ILCompiler and JIT) and
  `AirbenderLibsDir` (see bflat's `BUILDING.md`).
- `AIRBENDER_LIB_DIR`: a libairbender build
  ([NethermindEth/bflat-airbender](https://github.com/NethermindEth/bflat-airbender)),
  which holds the precompiles, the input and output, and the exit protocol.
- `bflat-riscv64`: the bflat build image, from bflat's `Dockerfile.build`.

```bash
make build BFLAT_DIR=<bflat>/src/bflat/bin/Release/net11.0 AIRBENDER_LIB_DIR=<libairbender>/output/airbender
```

The build writes `bin/nethermind` (ELF), `bin/nethermind.bin` (the ROM image) and
`bin/nethermind.text` (the instructions).

## Run

`make run` executes the guest on the Airbender CLI (`AIRBENDER_CLI`), built from
the zksync-airbender revision with the 4 MiB ROM:

```bash
make run INPUT=<fixture>.ssz AIRBENDER_CLI=<zksync-airbender>/target/cli/cli
```

The CLI prints the output words as `Result:`. The run does not generate or verify
a proof.

## Notes

- The image has no unwind tables. An exception goes to the shared `ZkvmThrow`
  handler, which publishes the failure result.
- The image has no reflection data, so the exception handler prints the message
  without the type name.

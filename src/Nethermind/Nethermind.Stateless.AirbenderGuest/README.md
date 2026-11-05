# Stateless Nethermind on Airbender

The guest targets ZKsync Airbender v3: RV32IM, a 4 MiB ROM, and a machine that
proves only the unsigned RV32M instructions. It publishes the Keccak-256 of the
validation result, as Airbender's public output is the eight words in x10-x17.
The full result is printed on the UART.

## Toolchain

`make build` compiles the guest in the bflat image with the riscv32 target
(`BFLAT_IMAGE`), against the Airbender platform and libairbender of a
[bflat-airbender](https://github.com/NethermindEth/bflat-airbender) release
(`AIRBENDER_VERSION`), which it downloads to `obj/`. libairbender holds the
precompiles, the input and output, and the exit protocol. `AIRBENDER_LIB_DIR`
names another libairbender build (its `output/airbender`) instead.

```bash
make build
```

bflat fails the build on an F, D, C or A instruction in the image, as
Airbender decodes RV32IM only, and on an image that does not fit the ROM.

The build writes `bin/nethermind` (ELF), `bin/nethermind.bin` (the ROM image) and
`bin/nethermind.text` (the instructions).

## Run

`make run` executes the guest on the Airbender CLI of the zksync-airbender
revision with the 4 MiB ROM, in the image that bflat-airbender publishes
(`AIRBENDER_RUNNER`). `AIRBENDER_CLI` names another CLI instead.

```bash
make run INPUT=<fixture>.ssz
```

The CLI prints the output words as `Result:`. The run does not generate or verify
a proof.

For a fixture pinned in `Nethermind.Stateless.ZiskGuest/inputs.json`, `make verify`
also checks the public digest and fails on a mismatch. Block `11962427.ssz`
exercises an unaligned fused PUSH2 jump target, which must work on RV32:

```bash
make verify INPUT=11962427.ssz
```

## Notes

- The image has no unwind tables. An exception goes to the shared `ZkvmThrow`
  handler, which publishes the failure result.
- The image has no reflection data, so the exception handler prints the message
  without the type name.
- The guest carries a frame's gas in 32 bits. A transaction with a gas limit of
  2^31 or more, possible only before EIP-7825 on a chain whose block gas limit
  is that high, stops the guest, which then publishes the failure result for a
  valid block.

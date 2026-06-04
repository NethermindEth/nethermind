# Stateless Nethermind

Building these projects requires Docker and a Linux environment with .NET installed.

### Projects

- [Nethermind.Stateless.ZiskGuest](./) : The Nethermind guest app intended to run exclusively on Zisk
- [Nethermind.Stateless.Executor](../Nethermind.Stateless.Executor/) : The core stateless execution and data serialization

### Build

To build an ELF binary for Zisk, run the following:

```bash
make build
```

The resulting binary can be found at `Nethermind.Stateless.ZiskGuest/bin/nethermind`.

To execute the Nethermind guest on Zisk, run the following:

```bash
make run INPUT=input.bin
```

There's also a combined command:

```bash
make build-run INPUT=input.bin
```

The `INPUT` variable must point to a file in the `Nethermind.Stateless.ZiskGuest/bin` directory. For details, see the [Makefile](./Makefile).

### Input serialization

The input data is a version-prefixed SSZ as specified [here](https://github.com/ethereum/execution-specs/blob/projects/zkevm/src/ethereum/forks/amsterdam/stateless_ssz.py): `schema: u16be | ssz_bytes`. For the pre-Amsterdam forks, `schema` is 0.

Starting from Zisk v0.16.0, the input data (`input.bin`) must be framed as follows when specified with the `--inputs` option:

```
len: u64le | bytes[len] | zero-padding
```

Zero-padding is required when the data length isn't a multiple of 8.

For the old behavior, use the `--legacy-inputs` option.

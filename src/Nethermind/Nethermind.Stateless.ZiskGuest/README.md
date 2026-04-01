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

The input data is a simple concatenation of 3 sections: `chain_id | block | witness`, as follows:

```
chain_id: u64le
block_rlp = len: i32le | bytes[len]
codes     = len: i32le | n: i32le | repeat n: (item_len: i32le | bytes[item_len])
headers   = len: i32le | n: i32le | repeat n: (item_len: i32le | bytes[item_len])
state     = len: i32le | n: i32le | repeat n: (item_len: i32le | bytes[item_len])
```

The block section is a regular RLP-encoded block prefixed with its length.

The witness section is a concatenation of 3 sections: codes, headers, and state.
Each section is a list of byte arrays, prefixed with the total length of the section and the number of items in the section.
The witness data comes from `debug_executionWitness` and is deserialized to the same format:

```json
{
  "codes": [[]],
  "headers": [[]],
  "state": [[]]
}
```

Starting from Zisk v0.16.0, the input data (`input.bin`) must be framed as follows when specified with the `--inputs` option:

```
len: u64le | bytes[len] | zero-padding
```

Zero-padding is required when the data length isn't a multiple of 8.

For the old behavior, use the `--legacy-inputs` option.

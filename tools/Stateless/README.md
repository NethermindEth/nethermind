# Stateless Nethermind

Building these projects requires Docker and a Linux environment with .NET installed.

### Projects

- [Executor](./Executor/) : The core stateless execution and data serialization
- [Tester](./Tester/) : A testing playground to verify execution on RISC-V
- [ZiskGuest](./ZiskGuest/) : The Nethermind guest app intended to run exclusively on Zisk

### Build

To build an ELF binary for Zisk, run the following:

```bash
make build-zisk
```

The resulting binary can be found at `ZiskGuest/bin/nethermind`.

To execute the Nethermind guest on Zisk, run the following (invokes the previous step automatically):

```bash
make run-zisk
```

For details, see the [Makefile](./Makefile).

### Input serialization

The input data (`input.bin`) is a simple concatenation of 3 sections: `chain_id | block | witness`, as follows:

```
chain_id: u32le
block_rlp = len: i32le | bytes[len]
codes     = len: i32le | n: i32le | repeat n: (item_len: i32le | bytes[item_len])
headers   = len: i32le | n: i32le | repeat n: (item_len: i32le | bytes[item_len])
keys      = len: i32le | n: i32le | repeat n: (item_len: i32le | bytes[item_len])
state     = len: i32le | n: i32le | repeat n: (item_len: i32le | bytes[item_len])
```

The block section is a regular RLP-encoded block prefixed with its length.

The witness section is a concatenation of 4 sections: codes, headers, keys, and state.
Each section is a list of byte arrays, prefixed with the total length of the section and the number of items in the section.
The witness data comes from `debug_executionWitness` and is deserialized to the same format:

```json
{
  "codes": [[]],
  "headers": [[]],
  "keys": [[]],
  "state": [[]]
}
```

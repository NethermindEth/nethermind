# Stateless Nethermind

Building these projects requires Docker and Linux with .NET installed.

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

The Zisk input data (input.bin) has a simple structure of `chain_id | block | witness` as follows:

```
chain_id: u32be
block_rlp = len: i32be | bytes[len]
codes     = len: i32be | n: i32be | repeat n: (item_len: i32be | bytes[item_len])
headers   = len: i32be | n: i32be | repeat n: (item_len: i32be | bytes[item_len])
keys      = len: i32be | n: i32be | repeat n: (item_len: i32be | bytes[item_len])
state     = len: i32be | n: i32be | repeat n: (item_len: i32be | bytes[item_len])
```

The witness data is deserialized as in `debug_executionWitness`:

```json
{
  "codes": [[]],
  "headers": [[]],
  "keys": [[]],
  "state": [[]]
}
```

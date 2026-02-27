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

The Zisk input data (input.bin) has a simple structure of `[ chain_id | block | witness ]` as follows:

```
[ chain_id (4) ]
[ section_len (4) | block_rlp (section_len) ]
[ section_len (4) | [ codes_count (4)   | [ count (4) | bytes (count) ] | ... ] ]
[ section_len (4) | [ headers_count (4) | [ count (4) | bytes (count) ] | ... ] ]
[ section_len (4) | [ keys_count (4)    | [ count (4) | bytes (count) ] | ... ] ]
[ section_len (4) | [ state_count (4)   | [ count (4) | bytes (count) ] | ... ] ]
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

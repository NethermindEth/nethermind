# Nethermind guest for Zisk

Building the guest app requires Linux and Docker.

### Usage

To build an ELF binary for Zisk, run the following:

```bash
make build-zisk
```

The resulting binary can be found in `bin/nethermind`.

To excute Nethermind guest in Zisk, run the following (invokes the previous step automatically):

```bash
make run-zisk
```

For details, see the [Makefile](./Makefile).

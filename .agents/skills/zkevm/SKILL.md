---
name: zkevm
description: Work on Nethermind's zkEVM stateless guests (ZisK, SP1, OpenVM) - layout, building and running guests, the stateless input/output format, guest cost, testing against tests-zkevm fixtures, adopting a new tests-zkevm release, and re-pinning the guests' .ssz inputs. Use for any change to the stateless executor or zkVM guests, or when asked to support a tests-zkevm release.
allowed-tools:
  [
    Bash,
    PowerShell,
    Read,
    Grep,
    Glob,
    Edit,
    Write,
  ]
---

# zkEVM stateless guests

A guest is Nethermind's stateless block validator, compiled with bflat into a RISC-V ELF for a zkVM. It reads one block with its execution witness and outputs whether the block is valid. The rules come from the `projects/zkevm` branch of ethereum/execution-specs, released as `tests-zkevm@*`.

## Layout

| Path | Role |
|---|---|
| `Nethermind.Stateless.Executor` | Guest entry point `StatelessExecutor.Execute(bytes)`, SSZ input decoding (`IO/`), spec provider selection |
| `Nethermind.Consensus/Stateless` | Witness decoding, witness-backed state and block tree, replay env. **Shared with the host**, so changes here affect host-side stateless replay too |
| `Nethermind.Stateless.{Zisk,Sp1,OpenVM}Guest` | Per-zkVM wrappers: `Program.cs` writes the output, the `Makefile` pins toolchain and runner images |
| `Nethermind.Stateless.Guest/` | Shared `Main`, `ZkvmThrow` failure protocol, `build.mk` compiler settings |
| `Nethermind.{Core,Trie,Evm}.ZkEvm.Test` | Unit tests of code compiled under `ZK_EVM` |
| `tools/StatelessInputGen` | Builds stateless inputs from a live node |

`-p:EnableZkEvm=true` defines `ZK_EVM` and trims the build to what a guest can run. Build-specific code lives in `*.std.cs` / `*.zkevm.cs` files (or `std/` / `zkevm/` folders), each compiled only in its build, rather than behind `#if ZK_EVM`. Code that differs per build is only exercised with the switch, so test it with the switch.

There is no exception unwinding in the zkVM runtime. A throw goes to `ZkvmThrow`, which writes `StatelessExecutor.FailureOutput`. That is why the failure result is encoded before anything that can throw.

## Build and run

The [shared guest guide](../../../src/Nethermind/Nethermind.Stateless.Guest.Shared/README.md) covers the commands, expected outputs, the framing each zkVM expects, and proving. In short, from a guest directory:

```bash
make build                      # dotnet build -p:EnableZkEvm=true, then bflat in Docker -> bin/nethermind
make run INPUT=<n>.ssz          # INPUT is a file name inside the guest's bin/
make stats INPUT=<n>.ssz        # ZisK only: exact step count and cost model
```

The Makefiles need GNU Make, Docker (linux/amd64), Python 3 (SP1/OpenVM strip the input frame with it) and the .NET SDK. If a Linux tool such as `make` is missing on Windows, try WSL. Its `make` target also calls `dotnet` itself, so either give WSL a .NET SDK or build .NET first and skip that step with `make -o dotnet-build build`.

## Input and output

- **Input:** `schema u16be | SSZ StatelessInput`. The schema id is fork index then revision: `0x0001` for the chain's current fork, `0x1501` for Amsterdam. The container lives in `IO/StatelessInput.cs` and mirrors `StatelessInput` in the spec's `stateless.py`. ZisK takes it framed as `len u64le | data | zero-padding to 8`; the SP1/OpenVM Makefiles strip the frame.
- **Output:** 43 bytes, `hash_tree_root(new_payload_request) (32) | success u8 | chain_id u64le | schema_id u16le`. If the input fails to decode, the output is all zeros. OpenVM publishes keccak256 of the output instead of the output itself.

## Guest cost

ZisK emulation is deterministic, so one `make stats` run is an exact measurement and even a small step delta is real. `stateless-benchmarks.yml` does this for every pinned block and compares a PR with master (`scripts/zisk-bench`). For cost work, compare steps on the same inputs before and after the change. The cost model counts precompile and accelerator calls, not just steps.

## Testing

- **Unit:** `Nethermind.Consensus.Test` (`Stateless` namespace), `StatelessSchemaTests` in `Ethereum.Blockchain.Pyspec.Test`, and the `*.ZkEvm.Test` projects, which need `-p:EnableZkEvm=true`.
- **Spec fixtures:** download `fixtures_zkevm.tar.gz` for the pinned release, then run `nethtest --zkevmTest -p 8 -i <fixture dir>`. It feeds each block's `statelessInputBytes` to `StatelessExecutor` and compares the result with `statelessOutputBytes`. Start from the groups most relevant to the change. `amsterdam/eip8025_optional_proofs` holds the stateless-specific tests. The execution-request and `eip7928` groups are cheap and have caught regressions. When a case fails:
  - **`…00…` expected, `…01…` got:** the spec rejects something the guest accepts.
  - **All zeros:** the input failed to decode.
- **CI-only:** the `ZkEvmBlockchainTests` Pyspec fixtures, which skip unless running on Linux x64. `StatelessExecutorOutputMatchesFixture` compares guest output over `blockchain_tests`, `WitnessMatchesFixture` compares the host-generated witness, and they cover the full fixture tree.
- **Not covered anywhere:** the guest bytes in `blockchain_tests_engine`. Those fixtures carry `statelessInputBytes`/`statelessOutputBytes` on each `engineNewPayloads[]` entry. Neither `nethtest` nor `StatelessExecutorOutputMatchesFixture` reads them (both read `blocks[]` of `blockchain_tests`). `ZkEvmEngineBlockchainTests` exercises the host's Engine API path (`engine_newPayloadWithWitness*`), not `StatelessExecutor`. The spec recommends guests use these fixtures because they cover more malformed-payload mutations. Until a runner reads them, don't treat a green CI as validating them.
- **Guests end to end:** `stateless-tests.yml` builds all three guests and runs them on the blocks pinned in `Nethermind.Stateless.ZiskGuest/inputs.json`. Locally, `make run` on one of those inputs must print `<output>0101000000000000000100`.

## Adopting a tests-zkevm release

1. **Read the diff:**
   ```bash
   gh release view <tag> -R ethereum/execution-specs
   gh api "repos/ethereum/execution-specs/compare/<previous>...<tag>" \
     --jq '.files[] | select(.filename | test("forks/amsterdam/(stateless|execution_engine|transactions|incremental_mpt|witness_state)")) | "=== \(.filename)\n\(.patch)"'
   ```
   Sort each change by its effect on the guest:
   - **Schema changes:** update `IO/`, and the pinned inputs go stale (see below).
   - **New validation rules.**
   - **Fixture fixes:** these often expose places where we were lenient because of an old fixture bug. `git grep -i "tests-zkevm"` finds code that cites the fixtures as its reason.
2. **Bump the pins:** `ArchiveVersion` in `Ethereum.Blockchain.Pyspec.Test/ZkEvmFixtures/Constants.cs`, and the two `zkevm_version` defaults plus `ZKEVM_VERSION` in `run-nethtest.yml`. Check that the fixtures' schema id (the first two bytes of `statelessInputBytes`) is still one `InputDecoder` accepts. A new revision means a new decode path.
3. **Update the code:** remove what the spec removes, end to end. That covers `IO/`, `StatelessPayload`, `InputDecoder`, `StatelessExecutor`, `tools/StatelessInputGen` and the tests. If the spec drops something that later guest code builds on, adapt that code rather than reverting the PR that added it.
4. **Run the fixtures** as in Testing. If the schema changed, re-pin the guests' inputs too.

## Re-pinning the guests' inputs

The pinned blocks in `Nethermind.Stateless.ZiskGuest/inputs.json` are ZisK-framed `.ssz` files. They are gitignored, live in that guest's `bin/`, and are downloaded from `https://us-southeast-1.linodeobjects.com/zktests/<yyyymmdd>/`. When the input schema changes:

1. **Convert** each file with an adapted copy of [`rewrite-input.js`](./rewrite-input.js), which is the v21.0.1 instance (it dropped `public_keys`). Write to a scratch directory and keep the originals. The script must reject anything that is not exactly the old layout.
2. **Test one input first:**
   - build the guest from the branch, then `make run` it on the converted file.
   - Run the unconverted file too, as a control. It must give the all-zero result, which proves the run can tell the two formats apart.
3. **Then all of them.** Replace the files in `bin/` only after every converted input passes.
4. **Publish.** Once the files are uploaded to a new bucket folder, check each upload with `curl -fsSL <url> | sha256sum` against the local file. Then update:
   - the `hash` values in `inputs.json`
   - the URL date in `stateless-tests.yml`
   - the URL date in `stateless-benchmarks.yml`. On a PR with changed hashes, the benchmark reads the head's `inputs.json` and downloads from this URL, so a stale date fails the checksum.

   `output` and `digest` change only if `NewPayloadRequest` itself changed.

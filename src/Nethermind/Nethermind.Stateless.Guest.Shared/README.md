# Stateless zkVM guests

The [ZisK](../Nethermind.Stateless.ZiskGuest/),
[SP1](../Nethermind.Stateless.Sp1Guest/), and
[OpenVM](../Nethermind.Stateless.OpenVmGuest/) projects compile the shared
Nethermind stateless executor for each host. Compiler settings are shared in
[`zkvm-guest.mk`](../zkvm-guest.mk); host Makefiles pin their execution images.

**`make run` executes the guest. It does not generate or verify a cryptographic
proof.** The pinned SP1 and OpenVM runner images currently provide execution
only. Adding `--gpus all` does not turn those runners into provers.

## Build and execute the standard block

Use Linux with Docker, GNU Make, Python 3, curl, and the .NET SDK required by
[`global.json`](../../../global.json). Docker must support Linux amd64 images.
A GPU is not required for these execution commands.

From the repository root, the following builds and executes mainnet block
25,532,480 on each host. This is a fixture from the existing
[`stateless-tests` workflow](../../../.github/workflows/stateless-tests.yml).

```bash
set -euo pipefail
for host in Zisk Sp1 OpenVm; do
    guest="src/Nethermind/Nethermind.Stateless.${host}Guest"
    make -C "$guest" build
    (
        cd "$guest/bin"
        curl -fsSL https://us-southeast-1.linodeobjects.com/zktests/20260819/25532480.ssz \
            -o 25532480.ssz
        echo 'e80386d5dc016a4b63bc6c24bf89ab636995c335dfcfdbea4df3554251c341d4  25532480.ssz' \
            | sha256sum -c -
    )
    make -C "$guest" run INPUT=25532480.ssz
done
```

Each ELF is written to its project's `bin/nethermind`. `INPUT` is a filename
relative to that project's `bin` directory, not an arbitrary input path.
`make build-run INPUT=25532480.ssz` combines the two targets once the fixture
has been placed there.

The fixture is framed as `length: u64le | payload[length] | zero-padding` to an
eight-byte boundary. ZisK consumes that frame. The SP1 and OpenVM Makefiles
remove it into `bin/25532480.ssz.raw` before invoking their runners. Do not strip
the frame before passing the fixture to `make run`.

The expected 43-byte successful result is:

```text
0bdd918dd79095dd95e5c68657ed418dee6043e4e4a1d5d5974d4824e24be8f70101000000000000000100
```

SP1 exposes the result directly. A ZisK emulator output file pads it with zeros.
OpenVM's fixed public-output window contains the **Keccak-256 hash** of the
result, not the result itself:

```text
79da63c269b732fc1ef197f2df0cbccba91c259256e39fd439fa85de9bcb0cfb
```

Check both the process exit status and the expected public output. Execution
success alone is not proof verification.

## GPU proving integration

The guests can be proved, but this repository does not yet expose supported
`make prove` or `make verify` targets. The current execution integrations are:

| Host | Execution integration | Input | Proving integration needed |
|---|---|---|---|
| ZisK | `nethermindeth/zisk`, pinned in the [Makefile](../Nethermind.Stateless.ZiskGuest/Makefile) | Framed fixture | Matching GPU worker, coordinator, and proving keys |
| SP1 | [bflat-sp1](https://github.com/NethermindEth/bflat-sp1/tree/main/runner) | Raw payload | CUDA-enabled SP1 SDK runner and matching GPU server |
| OpenVM | [bflat-openvm](https://github.com/NethermindEth/bflat-openvm/tree/main/runner) | Raw payload | CUDA-enabled OpenVM SDK runner and the guest's `openvm.toml` |

For ZisK, use matching versions of GPU binaries and proving keys; the emulator
image alone is not a complete GPU proving installation.
For SP1 and OpenVM, proof commands need to be added to those runners and new
images published before this repository can pin them. `SP1_RUNNER` and
`OPENVM_RUNNER` override the execution binary; they do not change the meaning
of `make run`.

The following constraints were exercised while proving this fixture locally
with ZisK 1.2.0-alpha, SP1 v6.5.0, and OpenVM rv64 revision
`56d5e4dbb545662f7ba3c31ac6c3e0ab1c6a481d`:

- Docker GPU access must work in the environment running the prover. Check
  `nvidia-smi` inside the intended CUDA container, and pass `--gpus all` when
  launching it.
- SP1 needs sufficient `/dev/shm` for its native executor. The execution
  Makefile defaults to `SP1_SHM_SIZE=8g`; use the same `--shm-size=8g` for a
  proving container. Empty output with a failing exit status can indicate
  insufficient shared memory.
- The official SP1 v6.5.0 GPU server links `libcudart.so.12`. A CUDA 13-only
  runtime does not supply that library. CUDA 12.9.1 was used for this server;
  OpenVM was compiled with CUDA 13.0.2 for the test GPU's SM 120 architecture.
- Use SP1's async CUDA client with its Tokio runtime alive during key and
  client destruction. A blocking-client harness produced valid proofs but
  panicked during cleanup when a destructor spawned a task without a runtime.
  An async harness reloaded and verified those proofs and exited successfully.
- Preserve OpenVM's pinned dependencies and guest `openvm.toml`. Its extension
  configuration is part of the guest's execution environment. Compare the
  verified public values against the digest above.
- Save proofs using buffered I/O and report serialization separately when
  measuring SDK proving calls. Unbuffered writes to a Windows bind mount can
  add substantial time outside the prover itself.

A runner change should validate a saved proof in a fresh invocation, bind
verification to the intended guest and configuration, check public values,
reject a corrupted proof and incorrect expected output, and exit cleanly.
Producing a proof file without those checks is insufficient validation.

## Comparing hosts

Use the same fixture checksum, Nethermind commit, shared compiler settings,
GPU, CPU quota, memory limit, and thread limits. Record each host's proof type,
version, CUDA build architecture, and stream or concurrency settings. Run one
host at a time, exclude a warm-up, and report multiple measured runs.

Separate guest compilation, prover/key setup, proving, serialization, and
verification. State whether verification starts a fresh process or reuses
loaded keys. A CLI request including proof-file writes is a different timing
boundary from an SDK proving call. Native proof types also need not have equal
security parameters, recursion work, or on-chain verification cost.

Low GPU utilization does not necessarily mean a fraction of the GPU was
allocated. In a partial 51-second sample of SP1 on an RTX PRO 6000 Blackwell,
utilization averaged 42.9% and peaked at 93% with the full device exposed.
CPU preparation, CPU quotas, and serialized proving stages can leave gaps;
that sample does not identify the limiting stage. Capture utilization over
the entire proof and compare CPU throttling and stage timings before changing
concurrency. Keep throughput tuning separate from a comparison using fixed
resource limits.

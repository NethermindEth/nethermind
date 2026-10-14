# EIP-8437 transport benchmarks

The harness runs separate Nethermind test processes over real RLPx/TCP or ethp2p/QUIC, with authenticated ETH66 transaction recovery. Linux network namespaces provide isolated, shaped links. Proof bytes, pool admission and verification are controlled fixtures; reported transaction goodput is **network capacity, not native proving or block throughput**. Native measurements and their reproduction recipe are in `native-cost/`.

Requirements: Linux, .NET 10 SDK, Python 3, `iproute2`, `ethtool`, and `libmsquic`. Namespace creation needs root. Use a dedicated machine or bound the workload; the default matrix uses at most four node processes and 100 Mbit/s links. It creates no external listener outside its namespaces.

```sh
dotnet build src/Nethermind/Nethermind.Network.Test/Nethermind.Network.Test.csproj -c Release
export LEAN_NET_PREFIX=lean14
export LEAN_NET_SUBNET=10.78
python3 scripts/lean-net-bench/netbench.py run --netns \
  --test-dll "$PWD/src/Nethermind/artifacts/bin/Nethermind.Network.Test/release/Nethermind.Network.Test.dll" \
  --binding ethp2p --kind 1 --transactions-per-object 16 \
  --proof-bytes 293448 --objects 24 --burst 4 --interval-ms 1000 \
  --rtt-ms 50 --rate-mbit 100 --out /tmp/lean-runs --results /tmp/lean-results.jsonl
```

Run `matrix.py --plan` to inspect the matrix before executing it. It compares loads and aggregation counts, 64 KiB through 8 MiB proofs, slower/lossy links, a four-node relay chain, and kind-2 broadcast at one object per 12-second slot. It records every attempted run; incomplete delivery and drain time reduce goodput. Never reuse a capture directory. Use a fresh results file when changing source or binaries.

```sh
python3 scripts/lean-net-bench/matrix.py \
  --test-dll "$PWD/src/Nethermind/artifacts/bin/Nethermind.Network.Test/release/Nethermind.Network.Test.dll" \
  --out /tmp/lean-runs --results /tmp/lean-results.jsonl --repetitions 2
python3 scripts/lean-net-bench/report.py /tmp/lean-results.jsonl --out /tmp/lean-report
```

The report requires `matplotlib`. Open `index.html` locally, or view the standalone PNG/SVG files on mobile. Each capture contains node logs, timings, interface counters, source revision, binary hash and runtime details. The controller stops its processes and removes its namespaces when finished; `netbench.py teardown` removes only the configured prefix after an interrupted run. Existing devnets and sites are not used.

The receive budget is not a process-memory limit: native prover workspace and benchmark fixtures must be measured separately. The cold native folding timings include recursive aggregation work, while root verification and fixture generation are reported independently. Neither the small final proof size nor a successful transport run demonstrates that the provisional 4096-dependency block limit is safe.

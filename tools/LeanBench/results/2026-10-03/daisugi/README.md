# Daisugi capture: 3 October 2026

Measured source `ca380a6eaf4dfa35ef75ff5269109fe7f04d7cde`, leanVM `f33f31bf7c1191667e29a68a3acae63b9164c1c6`; Apple M2 Max, 32 GiB RAM, 12 logical cores, macOS 15.7.4, .NET 10.0.9. Daisugi signatures are 6,176 bytes; witnesses include the 32-byte key (6,208 bytes). The resource-bounded prover uses four-signature leaves and binary recursion.

[Full rows](full/results.csv) · [Full JSON and 1,616 batch samples](full/results.json) · [Mixed rows](mixed/results.csv) · [Mixed JSON](mixed/results.json) · [Source, commands and fixture hashes](provenance.json)

## Proving and admission

Three measured crypto batches per size follow one warmup. Claims are distinct, with sixteen deterministic keys cycled across signed messages.

| SPHINCS claims | Prove mean | Verify mean | Proof size |
| ---: | ---: | ---: | ---: |
| 1 | 0.125 s | 10.55 ms | 259.1 KiB |
| 4 | 0.283 s | 10.96 ms | 270.5 KiB |
| 8 | 1.493 s | 14.64 ms | 320.4 KiB |
| 16 | 3.934 s | 15.16 ms | 289.0 KiB |
| 64 | 18.589 s | 16.85 ms | 294.8 KiB |
| 128 | 37.774 s | 16.44 ms | 305.2 KiB |

Four-signature batches had the best fresh proving-to-pool goodput among measured sizes: **12.7 tx/s**, versus **4.1 tx/s** for sixteen-signature batches under a target of 100 tx/s. Larger aggregates keep verification and proof size small while adding recursive proving work. These results describe this bounded implementation, not an intrinsic scaling limit of leanVM.

Load rows schedule arrivals for **three seconds**, with a FIFO capacity of two batches and one untimed warmup. Excess arrivals drop entire batches. Goodput uses elapsed time including drain; p95 includes queueing. These are short-window observations, not long-run capacity estimates. Pool entries are removed after admission without execution so funding and nonce state stay fixed.

## Preproved lean/2 transport

Distinct native proofs are prepared before timing and sent over encrypted localhost TCP through the real lean/2 chunk reassembly, background scheduler and pool admission. This path uses production codecs but excludes PacketSender; the mixed-traffic experiment below includes PacketSender. Each row has a three-second offered window, one warmup and measured drain.

| Claims per wrapper | Target tx/s | Actual offered tx/s | Accepted tx/s including drain | Ingress-dropped transactions |
| --- | ---: | ---: | ---: | ---: |
| 1 SPHINCS | 100 | 100.00 | 66.00 | 101 |
| 4 SPHINCS | 100 | 100.00 | 99.95 | 0 |
| 16 SPHINCS | 100 | 101.33 | 101.26 | 0 |
| 1 generic STARK | 100 | 100.00 | 71.54 | 85 |

Preparation is separately recorded (`preparationSeconds`). It is excluded from transport/admission goodput. Batch quantization explains actual rates above the target, such as 304 offered transactions for sixteen-signature wrappers over three seconds. Protocol latency percentiles include accepted batches only. Drops denote local ingress capacity or pending-wrapper replacement, without claiming one exclusive cause.

**Generic STARK generation is excluded from every timed row.** The fixture generator creates genuine CPU proofs; timed generic rows assemble an envelope, verify those carried witnesses and admit or transport it. Generic proofs are not recursively compressed. Fixture generation took 29.12 seconds for 2,048 SPHINCS claims and 512 generic proofs; per-proof generation costs are [recorded separately](fixture-generation.csv).

## Chunked mixed traffic

Three sequential transfers per row, with concurrent serialized ETH header requests, header responses and p2p pings every 20 ms. Production PacketSender, Snappy, AES/MAC RLPx and real TCP are used. Application pacing caps **encoded wire bytes** at 32 Mbit/s in 4 KiB socket-write quanta. This is not kernel shaping or a WAN/RTT/loss simulation. Control latency is scheduled-to-receiver delivery, not a round trip.

For the incompressible 10 MiB object, 64 KiB chunks reduced control p95 from **2,582.68 ms to 17.39 ms** (148.5×), while useful object goodput changed from **30.63 to 29.24 Mbit/s** (4.5% lower). All 24 rows had zero control/write drops. The measured maximum at 64 KiB was 44.61 ms, so p95 alone should not be read as a worst-case guarantee.

SPHINCS-1/16 and STARK-1 objects were genuinely proved and verified before mixed-traffic timing. `block-stark16` is a carried generic **block-proof envelope**, not a valid mempool wrapper: wrappers permit one generic dependency. Its whole-object control p95 was 615.78 ms, versus 15.27 ms at 64 KiB. Repeated delivery measures transport only, not pool admission or cryptographic goodput. Compressible generic objects show useful-payload goodput above 32 Mbit/s because the cap applies after Snappy; this is not a violation of the encoded-wire limit.

## Plots

[Crypto costs](full/plots/sphincs-batch-cost.png) · [Fresh admission goodput](full/plots/transaction-sphincs-admission.png) · [Preproved admission goodput](full/plots/protocol-sphincs-admission.png) · [Control latency](mixed/plots/chunk-control-latency.png) · [Object goodput](mixed/plots/chunk-object-goodput.png)

Historical October 2 and earlier October 3 captures remain unchanged. They use different source/profile snapshots and should not be treated as an isolated performance comparison with this Daisugi revision. Raw measurements retain source and compiled/native binary hashes; plotting was performed after all timed runs.

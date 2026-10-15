# Native cost captures

Real proof measurements for fork `cf1e92f87a7aec052b680f73e56145e866218583`, guest key `00f24bbdf44a31139c975223b77510cd93a905664da3c4a52b5eba4e9b07de9e`, adapter checkpoint `04da763081`. No Lean tuning. [Source and reproduction](../../../native-cost/README.md), [full provenance](provenance.json), [raw summary](summary.json).

[View graph (PNG)](native-aggregation.png) · [SVG](native-aggregation.svg). The graph shows medians and observed ranges; the table below shows means.

Apple M2 Max, 32 GiB RAM, one prover worker. Three fresh-process samples per small case, one exploratory SPH64 sample. Managed builds/tests overlapped part of capture; this was not an exclusively reserved timing host.

| Case | Samples | Mean aggregate seconds | Mean root verify ms | Root bytes | Maximum observed process RSS GiB |
|---|---:|---:|---:|---:|---:|
| sphincs1 | 3 | 1.051 | 46.650 | 265232 | 0.791 |
| sphincs16 | 3 | 33.364 | 44.423 | 293448 | 3.493 |
| generic1 | 3 | 17.164 | 80.119 | 313320 | 4.749 |
| mixed1_1 | 3 | 22.452 | 50.613 | 325248 | 4.960 |
| merge_parents | 3 | 7.250 | 44.298 | 325248 | 3.131 |
| sphincs64 | 1 | 169.839 | 43.162 | 291464 | 3.685 |

SPH16/64 are bounded native aggregation trees. Mixed raw input folds separate genuine leaves; prepared-parent merge excludes leaf construction from its fresh timed process. Aggregation includes input verification. Fixture preparation is recorded separately; peak RSS includes the whole process. These observed peaks are not upper RAM bounds.

Direct witness verification batch means (ten repetitions) are in each corresponding `.log`; they are separate from final root verification. The generic test is a trivial real program with default 2048-instruction geometry and full-width public-input packing. It does not cover worst allowed arbitrary programs or μ22 inner geometry, and cannot justify a universal dependency weight of 16. The active 256-dependency profile does not implement the draft aggregate ceiling of 4096.

Original absolute command paths and executable/source hashes are retained in immutable raw metadata. The reproduction recipe builds the same probe source against the pinned adapter/fork at another location; its executable hash may differ. No witnesses, parent proofs, root binaries or private signing data are tracked.


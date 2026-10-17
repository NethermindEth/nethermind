# Native live derivation regressions

These are the eight distinct accepted native batches captured from the
unchanged upstream protocol scenarios in Nethermind CI run 38982978228,
job 117247870500, artifact 12083888578, on 2026-10-17.

Upstream source: 2c19e36bed2720c00f89841ccfca47ec7c921e06.
Core source: 9a1f815336c20d28e5ec5cecbf195c41fbd5ab0f.

Each `.blob` file contains the original physical beacon blob bytes, in order.
Each JSON file records their SHA256, the L1 posting transaction, scenario
names, independently observed canonical L2 system transaction positions and
complete calldata, and the original signed outbound source transactions.
No blob, request, outcome, rolling hash or expected calldata is changed.

The corpus covers 34 eligible scenarios, including repeated calls, nested and
reentrant calls, failed terminal calls, static reads and source-frame rollback.
It checks deterministic transaction reconstruction; it does not replace the
dynamic live follower/recovery tests or authorize prover signing. The test
uses the original L2 chain ID 696990 and a synthetic local system nonce solely
to check that reconstructed native nonces remain consecutive.

Archive SHA256:
3f5816a5138d9f09902e4f6c7a9c699ba0067f7ad607d2a4ad4cbda59faa5a9d

The JSON fixtures also retain the original complete L1 posting calldata and its SHA256. Settlement transaction hashes match the physical blob transaction. These postings include their original proof bytes. Full L1 table reconstruction compares every canonical entry and static-entry ABI hash; supplied root updates seed the codec and do not authorize prover signing.

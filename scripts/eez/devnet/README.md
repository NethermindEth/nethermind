# Live EEZ integration tests

One CI job builds the candidate with the repository Dockerfile and runs the current public Rollup0 Kurtosis scenario driver with the candidate Nethermind follower. It checks the complete settled prefix, raw transactions, receipts and native accounting. It restarts Nethermind with its existing database, then adds a new follower with an empty independent database.

At run start, CI resolves upstream main once and reads its Core gitlink. The source and runtime artifact must match this exact revision. Results retain source revisions, artifact digest, binary checksums, candidate revision and runtime tool versions. An explicit upstream revision permits replay; it must belong to upstream main history. A missing matching artifact causes a build of the selected source, not a fallback to an older successful revision.

The temporary upstream overlay adds the Nethermind service and observations after the upstream scenario scripts. It keeps the current upstream driver, contracts, scenarios and assertions. New upstream tests are therefore consumed without manual source pin updates. A changed integration boundary fails explicitly.

The initial lane verifies the **legacy Rollup0 0x76 follower profile**. The upstream composer connects to the candidate Nethermind prove.v1 attester in this lane. The existing signer service name, port, deployment arguments and upstream assertions remain. The public deployment key is imported through stdin into an encrypted keystore; its address must match the deployed signer. The unchanged state-chaining tests correlate the signed height and digest with composer attestation and accepted L1 settlement. The signer outage test requires no settlement while the candidate is stopped and progress after restart. Live prove.v2 Resume/Rewind/Cancel and its independent canonical-parent authorization remain separate gates. RollupX and draft Rollup0 0x45 conformance require their own profiles and tests.

Results use an exact canonical L1 registry snapshot. The registered commitment is an L2 block hash. Both nodes must provide its entire ancestry and the same transaction bytes and receipts. Canonical L1 and L2 endpoints are checked again after observations. Missing data, empty required scenarios and any mismatch fail the lane.

The initial observation verifies zero native account balance and nonce at genesis on both nodes. Later observations reuse that passed result only when both nodes still have the same genesis hash, state root and chain ID. They do not require historical genesis state to remain available. Account state at the settled endpoint is still checked on both nodes in every phase.

PRs and pushes touching EEZ or its execution dependencies start the workflow. The daily schedule detects upstream-only changes after the workflow reaches the default branch. Manual runs can select an exact upstream revision.

The result records the candidate prover image ID, candidate revision and nonzero signed-window/remote-attestation counts in `candidate-prover.json`. The upstream image build is skipped through its existing switch; an image ID change fails the lane. This proves only the legacy Rollup0 profile and scenarios that actually run.

All nodes and Docker builds run on disposable CI runners. Cleanup removes only this run's named enclave and candidate images. Existing VM services are not used or modified.

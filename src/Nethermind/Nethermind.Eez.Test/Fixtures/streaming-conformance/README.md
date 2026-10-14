# Streaming conformance vectors

The JSON files are unchanged portable vectors from public Rollup0 PR205 at the revision and checksums in provenance.json. The source project licenses them under MIT OR Apache-2.0. They use a public isolated test signer and the captured six-block window79..84.

StreamingConformanceTests sends the recorded protobuf payloads through the actual Nethermind mTLS endpoint, execution backend, settlement verifier and signer. Expected responses, cursors, public-input hashes and signer identities remain upstream values. Only server-issued session IDs/epochs, diagnostic rejection text and cache-reuse reporting are normalized. Signatures are checked by recovery over the expected digest.

The production endpoint retains canonical L1/L2 parent authorization. Positive cases use predeclared fixture registration at recorded parent78,79,80 or83. Registration never follows an incoming request. Companion negative cases keep registration at parent78 and reject the unsettled subrange without signing. These tests establish wire compatibility under fixture authorization, not live conditional-profile equivalence or live external-composer interoperability.

CI resolves current public main and its matching Core gitlink once per job. After PR205 merges, it downloads the current vector data automatically. Before merge, the explicit reviewed snapshot is used and its distinct provenance is recorded. Missing vectors after merge fail CI. No contributed Rust code is executed. Local tests can use this committed snapshot; runtime verification runs in CI.

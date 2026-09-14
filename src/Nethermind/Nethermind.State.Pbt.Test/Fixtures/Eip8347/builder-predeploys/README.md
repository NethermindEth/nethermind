# Builder-predeploy fixture edition

This separately regenerated edition adds the two required EIP-8282 builder predeploys to the original fixture genesis. Both geth and Nethermind enable EIP-8282; no feature was disabled. The original edition remains unchanged one directory above.

Source: CPerezz/go-ethereum `e31a37fb88c2b75c0897c033bd3f4279dee42268`, exact `params.BuilderDepositAddress`/`BuilderDepositCode` and `params.BuilderExitAddress`/`BuilderExitCode`, each nonce 1 and balance 0. The optional test-only collector setting is `EIP8347_BUILDER_PREDEPLOYS=1`. `edition.json` records this choice.

All eleven lifecycle states, block/header/transaction/BAL bytes and hashes, MPT/PBT roots, legacy images and canonical EIP preimages were regenerated. Two complete runs produced 62 byte-identical files (312857 bytes). Existing reference state, receipt-success, conversion/root, strict legacy import verification, canonical parser and malformed-input assertions were retained. The original edition's generated files were compared to its published copies and remained unchanged.

Genesis hash: `0x2ff7c7cf2c5f53f4918be60a2bb129731f1a5f4d3baa0cdce75751a718764297`.
Anchor MPT root: `0x9dcca0bd9470e4838d495eeafd27564053af72ea3ceb5c107fa34b93221d996b`.
Anchor PBT root: `0x038c17361c0b8c818553c403e35aa7f1184d6ae6a3a40cdb834af116233d3356`.
Canonical anchor snapshot Keccak: `0x29addc9e40c479129697ac2ef5c52b082c6f21e74c3e8d1064aac61b289ea967`.
Canonical anchor preimages Keccak: `0x8741e97994a3ed88a6b982c6e0d648d16612f40e30a593152cc3c73ccfb6f223`.

For reproduction follow the parent README, installing this edition's lifecycle collector over the reference `eth/catalyst/eip8347_fixture_export_test.go`, using a separate output directory and setting `EIP8347_BUILDER_PREDEPLOYS=1` for the lifecycle test. Run lifecycle, image and canonical collectors, then repeat into another output and compare bytes. Image/canonical collectors are unchanged from the original edition.

The same oracle distinction applies: `images/` contains legacy geth RLP-preimage interoperability evidence; **`canonical/` is the current pinned EIP-8347 format**. Pinned geth verifies roots and legacy wire; the user-approved test-only canonical adapter independently parses/verifies exact current-format key sets. Post-fork images remain parity vectors rather than eligible migration anchors. See the parent README for coverage limits and upstream digest-log caveat.

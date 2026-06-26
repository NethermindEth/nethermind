// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;

namespace Ethereum.Blockchain.Pyspec.Test.ZkEvmPreview;

// The pinned zkVM fixture archive (tests-zkevm@v0.4.1) is generated against bal-devnet-7
// (tests-bal@v7.2.0), whose EIP-7928 block-access-list semantics differ from the devnet-6
// (tests-glamsterdam-devnet@v6.0.0) rules this stack implements. Stateless execution therefore
// rejects every fixture block on a BAL mismatch (IsSuccess=0), so the suite cannot pass here.
// Re-enable once the client tracks bal-devnet-7 (or the archive is repinned to devnet-6).
[Ignore("Pinned zkVM fixtures target bal-devnet-7; this stack implements devnet-6 BAL semantics.")]
public class ZkEvmBlockchainTests : ZkEvmBlockchainTestFixture;

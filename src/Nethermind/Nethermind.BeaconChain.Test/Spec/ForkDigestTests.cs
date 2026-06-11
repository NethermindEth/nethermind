// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Spec;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.Spec;

public class ForkDigestTests
{
    // Expected digests verified against the publicly known mainnet values (phase0/capella/deneb)
    // and an independent Python implementation of EIP-7892 for the Fulu/BPO entries.
    [TestCase(0ul, "0xb5303f2a")] // phase0
    [TestCase(194048ul, "0xbba4da96")] // capella
    [TestCase(269568ul, "0x6a95a1a9")] // deneb
    [TestCase(364032ul, "0xad532ceb")] // electra
    [TestCase(411392ul, "0xcc2c5cdb")] // fulu pre-BPO: Electra blob params at Electra epoch
    [TestCase(412671ul, "0xcc2c5cdb")] // last pre-BPO1 epoch
    [TestCase(412672ul, "0xcb0d1acc")] // BPO1: 15 blobs
    [TestCase(419072ul, "0x8c9f62fe")] // BPO2: 21 blobs
    [TestCase(500000ul, "0x8c9f62fe")] // beyond BPO2 the digest is stable
    public void Computes_mainnet_fork_digest(ulong epoch, string expected) =>
        Assert.That(ForkDigest.Compute(BeaconChainSpec.Mainnet, epoch), Is.EqualTo(Bytes.FromHexString(expected)));
}

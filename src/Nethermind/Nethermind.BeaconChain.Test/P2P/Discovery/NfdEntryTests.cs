// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.BeaconChain.Spec;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.P2P.Discovery;

/// <summary>
/// The ENR <c>nfd</c> entry (EIP-7892 / p2p-interface.md): "the digest of the next scheduled fork,
/// regular or blob-parameter-only", published as the raw 4-byte SSZ <c>ForkDigest</c>, or its
/// zero-filled default when none is scheduled.
/// </summary>
public class NfdEntryTests
{
    [Test]
    public void Key_is_nfd() =>
        Assert.That(new NfdEntry(Bytes.FromHexString("0xcb0d1acc")).Key, Is.EqualTo("nfd"));

    [Test]
    public void No_scheduled_fork_is_the_four_byte_zero_default() =>
        Assert.That(NfdEntry.NoneScheduled, Is.EqualTo(new byte[] { 0, 0, 0, 0 }));

    [Test]
    public void Value_is_carried_as_a_raw_rlp_byte_string()
    {
        // ForkDigest is a fixed 4-byte SSZ vector, so unlike an SSZ list or container its encoding is
        // exactly its own bytes: the entry's RLP payload must equal the digest with no extra framing.
        byte[] digest = Bytes.FromHexString("0xcb0d1acc");
        NfdEntry entry = new(digest);
        int keyLength = Rlp.LengthOf(entry.Key);

        byte[] buffer = new byte[keyLength + Rlp.LengthOf(digest) + 8];
        RlpWriter writer = new(buffer);
        entry.Encode(ref writer);

        Assert.That(buffer[keyLength..writer.Position], Is.EqualTo(Rlp.Encode(digest).Bytes));
    }

    // Real, shipped mainnet schedule (not a synthetic one): the two epochs either side of BPO1
    // (412672) and BPO2 (419072), plus beyond BPO2 where nothing further is currently scheduled.
    // Expected digests are the same values ForkDigestTests verifies against an independent
    // implementation, so this exercises EnrForkId.NextForkDigest against a source outside itself.
    [TestCase(412671ul, "0xcb0d1acc")] // last pre-BPO1 epoch: nfd already points at BPO1
    [TestCase(412672ul, "0x8c9f62fe")] // at BPO1: nfd now points at BPO2
    [TestCase(419071ul, "0x8c9f62fe")] // last pre-BPO2 epoch
    public void Next_fork_digest_matches_the_upcoming_mainnet_bpo_digest(ulong epoch, string expectedNfd) =>
        Assert.That(EnrForkId.NextForkDigest(BeaconChainSpec.Mainnet, epoch), Is.EqualTo(Bytes.FromHexString(expectedNfd)));

    [Test]
    public void Next_fork_digest_is_null_once_nothing_further_is_scheduled() =>
        Assert.That(EnrForkId.NextForkDigest(BeaconChainSpec.Mainnet, 419072ul), Is.Null);
}

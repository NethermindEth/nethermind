// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Encoding;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.RLP;
using Nethermind.Xdc.Types;
using NUnit.Framework;
using System.Collections.Generic;

namespace Nethermind.Xdc.Test;

[TestFixture]
public class SnapshotDecoderTests
{
    private static readonly IRlpDecoder<Snapshot> Decoder = new SnapshotDecoder();

    private static IEnumerable<TestCaseData> Snapshots => [
        new TestCaseData(new Snapshot(1, Keccak.EmptyTreeHash, [])).SetName("EmptySigners"),
        new TestCaseData(new Snapshot(3, Keccak.EmptyTreeHash, [Address.FromNumber(1), Address.FromNumber(2)])).SetName("WithSigners"),
    ];

    [Test, TestCaseSource(nameof(Snapshots))]
    public void RoundTrip(Snapshot original) =>
        Assert.That(Decoder.Decode(Decoder.Encode(original).Bytes), Is.EqualTo(original).UsingXdcComparer());

    [Test, TestCaseSource(nameof(Snapshots))]
    public void Decodes_with_a_trailing_item(Snapshot original) =>
        Assert.That(Decoder.Decode(RlpTestHelper.WithTrailingItem(Decoder.Encode(original).Bytes)), Is.EqualTo(original).UsingXdcComparer());

    [Test]
    public void Decode_throws_on_null_hash()
    {
        Snapshot snapshot = new(1, Keccak.EmptyTreeHash, []);
        snapshot.HeaderHash = null!;
        Rlp rlp = Decoder.Encode(snapshot);

        Assert.That(() => Decoder.Decode(rlp.Bytes), Throws.InstanceOf<RlpReaderException>());
    }

    [Test]
    public void Decode_throws_on_null_candidate()
    {
        // The encoder refuses a null address, so the empty string is written by hand.
        Rlp rlp = Rlp.Encode(Rlp.Encode(1UL), Rlp.Encode(Keccak.EmptyTreeHash), Rlp.Encode(new[] { Rlp.OfEmptyByteArray }));

        Assert.That(() => Decoder.Decode(rlp.Bytes), Throws.TypeOf<RlpReaderException>());
    }
}

// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
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

    [Test]
    public void Decode_throws_on_null_hash()
    {
        Snapshot snapshot = new(1, Keccak.EmptyTreeHash, []);
        snapshot.HeaderHash = null!;
        Rlp rlp = Decoder.Encode(snapshot);

        Assert.That(() => Decoder.Decode(rlp.Bytes), Throws.TypeOf<RlpException>());
    }

    [Test]
    public void Decode_throws_on_null_candidate()
    {
        Snapshot snapshot = new(1, Keccak.EmptyTreeHash, [null!]);
        Rlp rlp = Decoder.Encode(snapshot);

        Assert.That(() => Decoder.Decode(rlp.Bytes), Throws.TypeOf<RlpException>());
    }
}

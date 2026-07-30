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
}

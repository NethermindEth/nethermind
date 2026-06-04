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
public class SubnetSnapshotDecoderTests
{
    private static readonly IRlpDecoder<SubnetSnapshot> Decoder = new SubnetSnapshotDecoder();

    private static IEnumerable<TestCaseData> Snapshots => [
        new TestCaseData(new SubnetSnapshot(1, Keccak.EmptyTreeHash, [], [])).SetName("EmptySnapshot"),
        new TestCaseData(new SubnetSnapshot(3, Keccak.EmptyTreeHash, [Address.FromNumber(1), Address.FromNumber(2)], [Address.FromNumber(3), Address.FromNumber(4)])).SetName("WithSignersAndPenalties"),
    ];

    [Test, TestCaseSource(nameof(Snapshots))]
    public void RoundTrip(SubnetSnapshot original) =>
        Assert.That(Decoder.Decode(Decoder.Encode(original).Bytes), Is.EqualTo(original).UsingXdcComparer());
}

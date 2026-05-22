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

    private static IEnumerable<Snapshot> Snapshots => [
        new Snapshot(1, Keccak.EmptyTreeHash, []),
        new Snapshot(3, Keccak.EmptyTreeHash, [Address.FromNumber(1), Address.FromNumber(2)]),
    ];

    [Test, TestCaseSource(nameof(Snapshots))]
    public void RoundTrip(Snapshot original) =>
        Assert.That(Decoder.Decode(Decoder.Encode(original).Bytes), Is.EqualTo(original).UsingPropertiesComparer());
}

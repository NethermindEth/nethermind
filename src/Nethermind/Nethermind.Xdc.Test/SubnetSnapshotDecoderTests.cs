// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
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
    private static IEnumerable<SubnetSnapshot> Snapshots => [
        new SubnetSnapshot(1, Keccak.EmptyTreeHash, [], []),
        new SubnetSnapshot(3, Keccak.EmptyTreeHash, [Address.FromNumber(1), Address.FromNumber(2)], [Address.FromNumber(3), Address.FromNumber(4)]),
    ];

    [Test, TestCaseSource(nameof(Snapshots))]
    public void RoundTrip_ValueDecoder(SubnetSnapshot original)
    {
        SubnetSnapshotDecoder encoder = new();
        RlpStream rlpStream = new(encoder.GetLength(original, RlpBehaviors.None));
        encoder.Encode(rlpStream, original);
        rlpStream.Position = 0;

        Rlp.ValueDecoderContext ctx = rlpStream.Data.AsSpan().AsRlpValueContext();
        SubnetSnapshot decoded = encoder.Decode(ref ctx)!;
        if (original is null)
        {
            decoded.Should().BeNull();
        }
        else
        {
            decoded.Should().BeEquivalentTo(original);
        }
    }

    [Test, TestCaseSource(nameof(Snapshots))]
    public void RoundTrip_stream(SubnetSnapshot original)
    {
        SubnetSnapshotDecoder encoder = new();
        RlpStream stream = new(encoder.GetLength(original, RlpBehaviors.None));
        encoder.Encode(stream, original);

        stream.Reset();

        SubnetSnapshotDecoder decoder = new();
        SubnetSnapshot decoded = decoder.Decode(stream);
        decoded.Should().BeEquivalentTo(original);
    }
}

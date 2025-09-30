using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.RLP;
using Nethermind.Xdc.Types;
using NUnit.Framework;
using System.Collections;
using System.Collections.Generic;
using System.Text;

namespace Nethermind.Xdc.Test;

[TestFixture]
public class SnapshotDecoderTests
{
    private static IEnumerable<Snapshot> Snapshots => [
        new Snapshot(3, Keccak.EmptyTreeHash, [], [Address.FromNumber(1), Address.FromNumber(2)]),
    ];

    [Test, TestCaseSource(nameof(Objects))]
    public void RoundTrip_valuedecoder(Snapshot original)
    {
        SnapshotDecoder encoder = new();
        RlpStream rlpStream = new(encoder.GetLength(original, RlpBehaviors.None));
        encoder.Encode(rlpStream, original);
        rlpStream.Position = 0;

        Rlp.ValueDecoderContext ctx = rlpStream.Data.AsSpan().AsRlpValueContext();
        Snapshot decoded = encoder.Decode(ref ctx)!;
        if (original is null)
        {
            decoded.Should().BeNull();
        }
        else
        {
            decoded.Should().BeEquivalentTo(original);
        }
    }

    [Test, TestCaseSource(nameof(Objects))]
    public void RoundTrip_stream(Snapshot original)
    {
        SnapshotDecoder encoder = new();
        RlpStream stream = new(encoder.GetLength(original, RlpBehaviors.None));
        encoder.Encode(stream, original);

        stream.Reset();

        SnapshotDecoder decoder = new();
        Snapshot decoded = decoder.Decode(stream);
        decoded.Should().BeEquivalentTo(original);
    }
}

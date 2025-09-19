using FluentAssertions;
using NUnit.Framework;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

using Nethermind.Core.Test.Builders;
using Nethermind.Core.Extensions;
using System.Collections;
using Nethermind.Xdc.Types;
using Nethermind.Core.Crypto;
using System.Collections.Generic;
using Nethermind.Xdc.RLP;

namespace Nethermind.Xdc.Test
{
    [TestFixture]
    public class SnapshotDecoderTests
    {
        private static IEnumerable<Snapshot> Objects => [
            new Snapshot(1, Keccak.Zero, [Address.Zero, Address.FromNumber(1)]),
            new Snapshot(2, Keccak.MaxValue, [Address.FromNumber(1), Address.FromNumber(2)]),
            new Snapshot(3, Keccak.OfAnEmptySequenceRlp, []),
        ];

        [Test, TestCaseSource(nameof(Objects))]
        public void RoundTrip(Snapshot original)
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
}

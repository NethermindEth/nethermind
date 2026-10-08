// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Network.Rlpx.Handshake;
using NUnit.Framework;

namespace Nethermind.Network.Test.Rlpx.Handshake
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class Eip8MessagePadTests
    {
        // When NextInt returns 0 the pad length is the minimum (100 bytes);
        // when it returns maxValue-1 the pad length is the maximum (300 bytes).
        [TestCase(false, 100, Description = "Pads at least 100 bytes")]
        [TestCase(true, 300, Description = "Pads at most 300 bytes")]
        public void Pads_expected_length(bool useMaxRandom, int expectedPadding)
        {
            TestRandom testRandom = useMaxRandom
                ? new(static i => i - 1, static i => new byte[i])
                : new TestRandom(static i => 0, static i => new byte[i]);

            Eip8MessagePad pad = new(testRandom);

            Assert.That(pad.GetPaddingLength(), Is.EqualTo(expectedPadding));
        }

        [Test]
        public void Pad_fills_the_whole_span()
        {
            TestRandom testRandom = new(static i => 0, static length => Enumerable.Repeat((byte)7, length).ToArray());

            Eip8MessagePad pad = new(testRandom);
            byte[] padding = new byte[pad.GetPaddingLength()];
            pad.Pad(padding);

            Assert.That(padding, Is.All.EqualTo(7));
        }
    }
}

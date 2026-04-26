// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.Rlpx.Handshake;
using NUnit.Framework;

namespace Nethermind.Network.Test.Rlpx.Handshake
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class Eip8MessagePadTests
    {
        // When NextInt returns 0 the pad adds the minimum (100 bytes);
        // when it returns maxValue-1 it adds the maximum (300 bytes).
        [TestCase(false, 100, Description = "Adds at least 100 bytes")]
        [TestCase(true, 300, Description = "Adds at most 300 bytes")]
        public void Adds_expected_padding(bool useMaxRandom, int expectedPadding)
        {
            byte[] message = { 1 };
            int lengthBeforePadding = message.Length;

            TestRandom testRandom = useMaxRandom
                ? new(static i => i - 1, static i => new byte[i])
                : new TestRandom(static i => 0, static i => new byte[i]);

            Eip8MessagePad pad = new(testRandom);
            message = pad.Pad(message);

            Assert.That(message.Length, Is.EqualTo(lengthBeforePadding + expectedPadding), "incorrect length");
            Assert.That(message[0], Is.EqualTo(1), "first byte touched");
        }
    }
}

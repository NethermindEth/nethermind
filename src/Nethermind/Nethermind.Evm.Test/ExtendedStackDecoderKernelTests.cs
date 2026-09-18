// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;

namespace Nethermind.Evm.Test;

[TestFixture]
public class ExtendedStackDecoderKernelTests
{
    [Test]
    public void Single_decoder_matches_the_pinned_table_for_every_immediate()
    {
        for (int value = byte.MinValue; value <= byte.MaxValue; value++)
        {
            ExtendedStackSingleDecode actual = ExtendedStackDecoderKernel.DecodeSingle((byte)value);
            bool expectedValid = value <= 90 || value >= 128;

            using (Assert.EnterMultipleScope())
            {
                Assert.That(actual.IsValid, Is.EqualTo(expectedValid), $"validity for 0x{value:X2}");
                Assert.That(actual.Depth, Is.EqualTo((value + 145) % 256), $"depth for 0x{value:X2}");
            }
        }
    }

    [Test]
    public void Pair_decoder_matches_the_pinned_table_for_every_immediate()
    {
        for (int value = byte.MinValue; value <= byte.MaxValue; value++)
        {
            ExtendedStackPairDecode actual = ExtendedStackDecoderKernel.DecodePair((byte)value);
            int shifted = value ^ 143;
            int quotient = shifted / 16;
            int remainder = shifted % 16;
            int expectedFirst = (quotient < remainder ? quotient : remainder) + 2;
            int expectedSecond = quotient < remainder ? remainder + 2 : 30 - quotient;
            bool expectedValid = value <= 81 || value >= 128;

            using (Assert.EnterMultipleScope())
            {
                Assert.That(actual.IsValid, Is.EqualTo(expectedValid), $"validity for 0x{value:X2}");
                Assert.That(actual.FirstPosition, Is.EqualTo(expectedFirst), $"first position for 0x{value:X2}");
                Assert.That(actual.SecondPosition, Is.EqualTo(expectedSecond), $"second position for 0x{value:X2}");
            }
        }
    }
}

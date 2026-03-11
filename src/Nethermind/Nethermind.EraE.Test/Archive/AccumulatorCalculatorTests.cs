// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.EraE.Archive;
using NUnit.Framework;

namespace Nethermind.EraE.Test.Archive;

public class AccumulatorCalculatorTests
{
    [Test]
    public void Add_AddOneHashAndUInt256_DoesNotThrow()
    {
        using var sut = new AccumulatorCalculator();
        Assert.That(() => sut.Add(Keccak.Zero, 0), Throws.Nothing);
    }

    [Test]
    public void ComputeRoot_KnownValues_ReturnsExpectedResult()
    {
        using var sut = new AccumulatorCalculator();
        sut.Add(Keccak.Zero, 1);
        sut.Add(Keccak.MaxValue, 2);

        byte[] result = sut.ComputeRoot().ToByteArray();

        // SSZ hash_tree_root of List(HeaderRecord, 8192) — same algorithm as Era1
        Assert.That(result, Is.EquivalentTo(new byte[]
        {
            0x3E, 0xD6, 0x26, 0x52, 0xDF, 0xB7, 0xE1, 0x07,
            0x2D, 0x0F, 0x04, 0x0F, 0xEB, 0x6D, 0x00, 0x2A,
            0x9F, 0x7C, 0xE3, 0x7C, 0xF8, 0xDD, 0xB1, 0x65,
            0x49, 0xA7, 0xAC, 0x5C, 0xF8, 0xE3, 0xB7, 0x91
        }));
    }

    [Test]
    public void ComputeRoot_SameInputAsDifferentInstances_ProducesSameResult()
    {
        using var sut1 = new AccumulatorCalculator();
        using var sut2 = new AccumulatorCalculator();

        sut1.Add(Keccak.Zero, 100);
        sut2.Add(Keccak.Zero, 100);

        Assert.That(sut1.ComputeRoot(), Is.EqualTo(sut2.ComputeRoot()));
    }

    [Test]
    public void ComputeRoot_DifferentInputs_ProducesDifferentResults()
    {
        using var sut1 = new AccumulatorCalculator();
        using var sut2 = new AccumulatorCalculator();

        sut1.Add(Keccak.Zero, 1);
        sut2.Add(Keccak.MaxValue, 1);

        Assert.That(sut1.ComputeRoot(), Is.Not.EqualTo(sut2.ComputeRoot()));
    }
}

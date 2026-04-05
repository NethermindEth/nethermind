// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using AccumulatorCalculator = Nethermind.Era1.AccumulatorCalculator;
using NUnit.Framework;

namespace Nethermind.EraE.Test.Archive;

public class AccumulatorCalculatorTests
{
    [Test]
    public void Add_WhenCalled_DoesNotThrow()
    {
        using AccumulatorCalculator sut = new();
        Assert.That(() => sut.Add(Keccak.Zero, 0), Throws.Nothing);
    }

    [Test]
    public void ComputeRoot_WithKnownValues_ReturnsExpectedResult()
    {
        using AccumulatorCalculator sut = new();
        sut.Add(Keccak.Zero, 1);
        sut.Add(Keccak.MaxValue, 2);

        byte[] result = sut.ComputeRoot().ToByteArray();

        Assert.That(result, Is.EquivalentTo(new byte[]
        {
            0x3E, 0xD6, 0x26, 0x52, 0xDF, 0xB7, 0xE1, 0x07,
            0x2D, 0x0F, 0x04, 0x0F, 0xEB, 0x6D, 0x00, 0x2A,
            0x9F, 0x7C, 0xE3, 0x7C, 0xF8, 0xDD, 0xB1, 0x65,
            0x49, 0xA7, 0xAC, 0x5C, 0xF8, 0xE3, 0xB7, 0x91
        }));
    }

    [Test]
    public void ComputeRoot_WithSameInputInTwoInstances_ReturnsSameResult()
    {
        using AccumulatorCalculator sut1 = new();
        using AccumulatorCalculator sut2 = new();

        sut1.Add(Keccak.Zero, 100);
        sut2.Add(Keccak.Zero, 100);

        Assert.That(sut1.ComputeRoot(), Is.EqualTo(sut2.ComputeRoot()));
    }

    [Test]
    public void ComputeRoot_WithDifferentInputs_ReturnsDifferentResults()
    {
        using AccumulatorCalculator sut1 = new();
        using AccumulatorCalculator sut2 = new();

        sut1.Add(Keccak.Zero, 1);
        sut2.Add(Keccak.MaxValue, 1);

        Assert.That(sut1.ComputeRoot(), Is.Not.EqualTo(sut2.ComputeRoot()));
    }

    [Test]
    public void GetProof_WithNegativeIndex_ThrowsArgumentOutOfRangeException()
    {
        using AccumulatorCalculator sut = new();
        sut.Add(Keccak.Zero, 1);

        Assert.That(() => sut.GetProof(-1), Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void GetProof_WithIndexAtCount_ThrowsArgumentOutOfRangeException()
    {
        using AccumulatorCalculator sut = new();
        sut.Add(Keccak.Zero, 1);

        Assert.That(() => sut.GetProof(1), Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void GetProof_WhenCalled_Returns15Elements()
    {
        using AccumulatorCalculator sut = new();
        sut.Add(Keccak.Zero, 42);

        Assert.That(sut.GetProof(0), Has.Length.EqualTo(15));
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(7)]
    public void GetProof_WhenCalled_ProofZeroIsTotalDifficultyLE(int blockIndex)
    {
        using AccumulatorCalculator sut = new();
        for (int i = 0; i <= blockIndex; i++)
            sut.Add(Keccak.Zero, (ulong)(i + 1));

        byte[] expected = new byte[32];
        expected[0] = (byte)(blockIndex + 1);
        Assert.That(sut.GetProof(blockIndex)[0].ToByteArray(), Is.EqualTo(expected));
    }

    [Test]
    public void GetProof_WithDifferentIndices_ReturnDifferentProofs()
    {
        using AccumulatorCalculator sut = new();
        sut.Add(Keccak.Zero, 1);
        sut.Add(Keccak.MaxValue, 2);

        ValueHash256[] proof0 = sut.GetProof(0);
        ValueHash256[] proof1 = sut.GetProof(1);

        Assert.That(proof0[0], Is.Not.EqualTo(proof1[0]));
        Assert.That(proof0[1], Is.Not.EqualTo(proof1[1]));
    }

}

// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;

namespace Nethermind.Era1.Test;
public class AccumulatorCalculatorTests
{
    [Test]
    public void Add_AddOneHashAndUInt256_DoesNotThrow()
    {
        var sut = new AccumulatorCalculator();

        Assert.That(() => sut.Add(Keccak.Zero, 0), Throws.Nothing);
    }
    [Test]
    public void ComputeRoot_AddValues_ReturnsExpectedResult()
    {
        var sut = new AccumulatorCalculator();
        sut.Add(Keccak.Zero, 1);
        sut.Add(Keccak.MaxValue, 2);

        var result = sut.ComputeRoot().ToArray();

        Assert.That(result, Is.EquivalentTo(new[] { 0x3E, 0xD6, 0x26, 0x52, 0xDF, 0xB7, 0xE1, 0x07, 0x2D, 0x0F, 0x04, 0x0F, 0xEB, 0x6D, 0x00, 0x2A, 0x9F, 0x7C, 0xE3, 0x7C, 0xF8, 0xDD, 0xB1, 0x65, 0x49, 0xA7, 0xAC, 0x5C, 0xF8, 0xE3, 0xB7, 0x91 }));
    }

    [Test]
    public void ComputeRoot_AddOneHashAndUInt256_DoesNotThrow()
    {
        var sut = new AccumulatorCalculator();
        sut.Add(Keccak.Zero, 1);
        sut.Add(Keccak.MaxValue, 2);

        var result = sut.ComputeRoot().ToArray();

        Assert.That(result, Is.EquivalentTo(new[] { 0x3E, 0xD6, 0x26, 0x52, 0xDF, 0xB7, 0xE1, 0x07, 0x2D, 0x0F, 0x04, 0x0F, 0xEB, 0x6D, 0x00, 0x2A, 0x9F, 0x7C, 0xE3, 0x7C, 0xF8, 0xDD, 0xB1, 0x65, 0x49, 0xA7, 0xAC, 0x5C, 0xF8, 0xE3, 0xB7, 0x91 }));
    }
}

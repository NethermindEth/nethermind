// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

[TestFixture]
public class Eip7843Tests : VirtualMachineTestsBase
{
    protected override long BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.AmsterdamBlockTimestamp;

    // public static IEnumerable<TestCaseData<UInt256>> Tests
    // {
    //     get
    //     {
    //         yield return new TestCaseData<UInt256>(UInt256.Zero) { ExpectedResult = 256 };
    //         yield return new TestCaseData<UInt256>(255) { ExpectedResult = 248 };
    //         yield return new TestCaseData<UInt256>(256) { ExpectedResult = 247 };
    //         yield return new TestCaseData<UInt256>(new UInt256(ulong.MaxValue)) { ExpectedResult = 192 };
    //         yield return new TestCaseData<UInt256>(new UInt256(ulong.MaxValue, ulong.MaxValue)) { ExpectedResult = 128 };
    //         yield return new TestCaseData<UInt256>(new UInt256(ulong.MaxValue, ulong.MaxValue, ulong.MaxValue)) { ExpectedResult = 64 };
    //         yield return new TestCaseData<UInt256>(new UInt256(ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, ulong.MaxValue)) { ExpectedResult = 0 };
    //         yield return new TestCaseData<UInt256>(new UInt256(ulong.MaxValue, ulong.MaxValue, ulong.MaxValue / 2, ulong.MaxValue >> 1)) { ExpectedResult = 1 };
    //         yield return new TestCaseData<UInt256>(new UInt256(0, 0, 0, ulong.MaxValue >> 4)) { ExpectedResult = 4 };

    //         for (int i = 0; i <= 255; i++)
    //         {
    //             yield return new TestCaseData<UInt256>(UInt256.One << i) { ExpectedResult = 255 - i };
    //         }
    //     }
    // }

    // [TestCaseSource(nameof(Tests))]
    [Test]
    public void SLOTNUMTest()
    {
        const long GasCostOfCallingWrapper = GasCostOf.Transaction + GasCostOf.VeryLow * 4 + GasCostOf.Memory;
        const ulong SlotNumber = 1000;

        byte[] code = Prepare.EvmCode
            .SLOTNUM()
            .MSTORE(0)
            .Return(32, 0)
            .Done;

        TestAllTracerWithOutput result = Execute(Activation, 50000, SlotNumber, code);

        Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
        AssertGas(result, GasCostOfCallingWrapper + GasCostOf.Base);

        byte[] expected = BitConverter.GetBytes(SlotNumber).PadLeft(32);
        Assert.That(result.ReturnValue, Is.EquivalentTo(expected));
    }
}

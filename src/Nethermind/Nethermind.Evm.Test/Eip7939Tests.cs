// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Int256;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class Eip7939Tests : VirtualMachineTestsBase
{
    protected override long BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.OsakaBlockTimestamp;

    public static IEnumerable<TestCaseData<UInt256>> Tests
    {
        get
        {
            yield return new TestCaseData<UInt256>(UInt256.Zero) { ExpectedResult = 256 };
            yield return new TestCaseData<UInt256>(255) { ExpectedResult = 248 };
            yield return new TestCaseData<UInt256>(256) { ExpectedResult = 247 };
            yield return new TestCaseData<UInt256>(new UInt256(ulong.MaxValue)) { ExpectedResult = 192 };
            yield return new TestCaseData<UInt256>(new UInt256(ulong.MaxValue, ulong.MaxValue)) { ExpectedResult = 128 };
            yield return new TestCaseData<UInt256>(new UInt256(ulong.MaxValue, ulong.MaxValue, ulong.MaxValue)) { ExpectedResult = 64 };
            yield return new TestCaseData<UInt256>(new UInt256(ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, ulong.MaxValue)) { ExpectedResult = 0 };
            yield return new TestCaseData<UInt256>(new UInt256(ulong.MaxValue, ulong.MaxValue, ulong.MaxValue / 2, ulong.MaxValue >> 1)) { ExpectedResult = 1 };
            yield return new TestCaseData<UInt256>(new UInt256(0, 0, 0, ulong.MaxValue >> 4)) { ExpectedResult = 4 };

            for (int i = 0; i <= 255; i++)
            {
                yield return new TestCaseData<UInt256>(UInt256.One << i) { ExpectedResult = 255 - i };
            }
        }
    }

    [TestCaseSource(nameof(Tests))]
    public int CLZTest(UInt256 value)
    {
        const long GasCostOfCallingWrapper = GasCostOf.Transaction + GasCostOf.VeryLow * 5 + GasCostOf.Memory;

        byte[] code = Prepare.EvmCode
            .PushData(value)
            .CLZ()
            .MSTORE(0)
            .Return(32, 0)
            .Done;

        TestAllTracerWithOutput result = Execute(Activation, 50000, code);

        result.StatusCode.Should().Be(StatusCode.Success);
        AssertGas(result, GasCostOfCallingWrapper + GasCostOf.VeryLow);
        return (int)new UInt256(result.ReturnValue, true);

    }
}

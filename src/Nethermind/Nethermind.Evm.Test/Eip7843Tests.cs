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

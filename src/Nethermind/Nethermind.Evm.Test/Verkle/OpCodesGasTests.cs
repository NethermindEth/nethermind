// SPDX-FileCopyrightText:2023 Demerzel Solutions Limited
// SPDX-License-Identifier:LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Evm.Test.Verkle;

[TestFixture]
public class OpCodesGasTests : VerkleVirtualMachineTestsBase
{

    [Test]
    public void TestGasCostUpdateForBalanceOpCode()
    {
        TestState.CreateAccount(TestItem.AddressC, 100.Ether());

        byte[] code = Prepare.EvmCode
            .PushData(TestItem.AddressC)
            .Op(Instruction.BALANCE)
            .Done;

        TestAllTracerWithOutput result = Execute(code);
        AssertGas(result, 21000 + GasCostOf.VeryLow +
                          GasCostOf.WitnessChunkRead + // this chunk cost is for code chunk read
                          GasCostOf.WitnessBranchRead + GasCostOf.WitnessChunkRead); // this is for the balance access cost
    }
    [Test]
    public void TestGasCostUpdateForCodeOpCodes()
    {

    }
    [Test]
    public void TestGasCostUpdateForStorageAccessAndUpdate()
    {

    }

    protected override TestAllTracerWithOutput CreateTracer()
    {
        TestAllTracerWithOutput tracer = base.CreateTracer();
        tracer.IsTracingAccess = false;
        return tracer;
    }
}

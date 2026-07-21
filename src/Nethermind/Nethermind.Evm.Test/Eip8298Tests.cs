// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// Tests for EIP-8298: SETCODEFROM code reuse instruction.
/// </summary>
public class Eip8298Tests : VirtualMachineTestsBase
{
    private static readonly Address Source = TestItem.AddressC;
    private static readonly byte[] SourceCode = Prepare.EvmCode.PushData(1).PushData(2).Op(Instruction.ADD).STOP().Done;

    protected override long BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.AmsterdamBlockTimestamp;
    protected override ISpecProvider SpecProvider => new TestSpecProvider(new Amsterdam { IsEip8298Enabled = true });

    // Disable access tracing so cold/warm account access is charged per EIP-2929
    // (the default tracer pre-warms accesses, masking the cold cost in gas assertions).
    protected override TestAllTracerWithOutput CreateTracer() => new() { IsTracingAccess = false };

    private void DeploySource(byte[]? code = null)
    {
        TestState.CreateAccount(Source, 1.Ether);
        TestState.InsertCode(Source, code ?? SourceCode, Spec);
    }

    [Test]
    public void ValidSource_AdoptsCodeHash_AndPushesOne()
    {
        DeploySource();
        byte[] code = Prepare.EvmCode.SETCODEFROM(Source).MSTORE(0).Return(32, 0).Done;

        TestAllTracerWithOutput result = Execute(code);

        Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
        Assert.That(new UInt256(result.ReturnValue, true), Is.EqualTo(UInt256.One));
        // Running frame keeps its loaded code, but the stored code hash now matches the source.
        AssertCodeHash(Recipient, Keccak.Compute(SourceCode));
    }

    private static object[] InvalidSourceCases =
    {
        new object[] { "empty", null!, false },
        new object[] { "eip7702-delegation", new byte[] { 0xef, 0x01, 0x00, 0x11, 0x22 }, true },
        new object[] { "eip3541-ef", new byte[] { 0xef, 0x00 }, true },
    };

    [TestCaseSource(nameof(InvalidSourceCases))]
    public void InvalidSource_PushesZero_AndLeavesCodeUnchanged(string name, byte[]? sourceCode, bool deploy)
    {
        if (deploy) DeploySource(sourceCode);
        byte[] code = Prepare.EvmCode.SETCODEFROM(Source).MSTORE(0).Return(32, 0).Done;

        TestAllTracerWithOutput result = Execute(code);

        Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success), name);
        Assert.That(new UInt256(result.ReturnValue, true), Is.EqualTo(UInt256.Zero), name);
        AssertCodeHash(Recipient, Keccak.Compute(code));
    }

    [Test]
    public void PrecompileSource_PushesZero()
    {
        byte[] code = Prepare.EvmCode.SETCODEFROM(Sha256Precompile.Address).MSTORE(0).Return(32, 0).Done;

        TestAllTracerWithOutput result = Execute(code);

        Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
        Assert.That(new UInt256(result.ReturnValue, true), Is.EqualTo(UInt256.Zero));
    }

    [Test]
    public void StaticContext_ExceptionalHalt()
    {
        DeploySource();
        TestState.CreateAccount(TestItem.AddressD, 1.Ether);
        TestState.InsertCode(TestItem.AddressD, Prepare.EvmCode.SETCODEFROM(Source).STOP().Done, Spec);

        byte[] code = Prepare.EvmCode.StaticCall(TestItem.AddressD, 50000).MSTORE(0).Return(32, 0).Done;

        TestAllTracerWithOutput result = Execute(code);

        Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
        // The static inner frame halts exceptionally, so STATICCALL reports failure.
        Assert.That(new UInt256(result.ReturnValue, true), Is.EqualTo(UInt256.Zero));
    }

    [Test]
    public void Initcode_ExceptionalHalt()
    {
        DeploySource();
        byte[] initCode = Prepare.EvmCode.SETCODEFROM(Source).STOP().Done;
        (Block block, Transaction tx) = PrepareInitTx(Activation, 100000, initCode);

        TestAllTracerWithOutput tracer = CreateTracer();
        _processor.Execute(tx, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Failure));
    }

    private static long ColdGas => GasCostOf.Transaction + GasCostOf.VeryLow + GasCostOf.SetCodeFromBase + GasCostOf.ColdAccountAccess;

    [Test]
    public void ColdSource_ChargesBasePlusColdAccess()
    {
        DeploySource();
        byte[] code = Prepare.EvmCode.SETCODEFROM(Source).STOP().Done;

        TestAllTracerWithOutput result = Execute(Activation, 100000, code);

        Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
        AssertGas(result, ColdGas);
    }

    [Test]
    public void WarmSource_SecondAccessChargesWarm()
    {
        DeploySource();
        byte[] code = Prepare.EvmCode.SETCODEFROM(Source).SETCODEFROM(Source).STOP().Done;

        TestAllTracerWithOutput result = Execute(Activation, 100000, code);

        Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
        AssertGas(result, ColdGas + GasCostOf.VeryLow + GasCostOf.SetCodeFromBase + GasCostOf.WarmStateRead);
    }

    public class Eip8298DisabledTests : VirtualMachineTestsBase
    {
        protected override long BlockNumber => MainnetSpecProvider.ParisBlockNumber;
        protected override ulong Timestamp => MainnetSpecProvider.AmsterdamBlockTimestamp;
        protected override ISpecProvider SpecProvider => new TestSpecProvider(new Amsterdam { IsEip8298Enabled = false });

        [Test]
        public void Opcode_WhenDisabled_Fails()
        {
            byte[] code = Prepare.EvmCode.SETCODEFROM(TestItem.AddressC).STOP().Done;
            TestAllTracerWithOutput result = Execute(code);
            Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Failure));
        }
    }
}

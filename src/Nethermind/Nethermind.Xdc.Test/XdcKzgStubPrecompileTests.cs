// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Precompiles;
using Nethermind.Evm;
using Nethermind.Evm.Test;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Xdc.Spec;
using NUnit.Framework;

namespace Nethermind.Xdc.Test;

[TestFixture]
public class XdcKzgStubPrecompileTests : VirtualMachineTestsBase
{
    private const long CallGas = 60_000;

    [SetUp]
    public override void Setup()
    {
        base.Setup();
        CacheCodeInfoRepository codeInfoRepository = new(TestState, new XdcPrecompileProvider(new EthereumPrecompileProvider()), StaticCodeCache.Instance);
        _processor = new EthereumTransactionProcessor(BlobBaseFeeCalculator.Instance, SpecProvider, TestState, Machine, codeInfoRepository, LimboLogs.Instance);
    }

    // Before Prague the reference client has no precompile at 0x0a, so even with EIP-4844 on the call reaches an empty
    // account and succeeds. From Prague it reaches the stub, which fails a well-formed input and burns the frame's gas.
    [Test]
    public void Call_to_point_evaluation([Values] bool prague)
    {
        byte[] code = Prepare.EvmCode
            .CallWithInput(PrecompiledAddresses.PointEvaluation, CallGas, new byte[192])
            .PushData(0)
            .Op(Instruction.SSTORE)
            .Done;
        (Block block, Transaction transaction) = PrepareTx(Activation, 200_000UL, code);
        XdcReleaseSpec spec = new() { IsEip4844Enabled = true, IsEip2537Enabled = prague };

        TestAllTracerWithOutput tracer = CreateTracer();
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, spec), tracer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(spec.IsPrecompile(PrecompiledAddresses.PointEvaluation), Is.EqualTo(prague));
            Assert.That(tracer.Error, Is.Null);
            AssertStorage(UInt256.Zero, prague ? UInt256.Zero : UInt256.One);
            Assert.That(tracer.GasSpent > GasCostOf.Transaction + CallGas, Is.EqualTo(prague));
        }
    }
}

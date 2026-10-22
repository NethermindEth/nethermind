// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.Test;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Taiko.Precompiles;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Taiko.Test;

/// <summary>
/// A KZG setup fault or running out of memory in a context-aware precompile is a node fault, so it must escape the Taiko VM's
/// precompile dispatch that turns every other exception into a failed call.
/// </summary>
public class TaikoPrecompileFaultTests : VirtualMachineTestsBase
{
    private const int CallGas = 100_000;

    private TaikoEthereumVirtualMachine _taikoMachine = null!;

    [TearDown]
    public override void TearDown()
    {
        _taikoMachine?.Dispose();
        base.TearDown();
    }

    [Test]
    public void Node_fault_escapes_the_context_aware_precompile_call([ValueSource(nameof(NodeFaults))] Exception fault)
    {
        UseContextAwarePrecompile(new ThrowingPrecompile(fault));

        Assert.That(ExecuteCall, Throws.TypeOf(fault.GetType()));
    }

    private static IEnumerable<Exception> NodeFaults()
    {
        yield return new KzgSetupUnavailableException("KZG trusted setup failed to load");
        yield return new OutOfMemoryException();
        yield return new TypeInitializationException("Precompile", new OutOfMemoryException());
    }

    [Test]
    public void Other_context_aware_precompile_exception_fails_the_call()
    {
        UseContextAwarePrecompile(new ThrowingPrecompile(new InvalidOperationException("malformed input")));

        Assert.That(ExecuteCall(), Is.False);
    }

    private void UseContextAwarePrecompile(IPrecompile precompile)
    {
        Dictionary<AddressAsKey, CodeInfo> precompiles = new(new EthereumPrecompileProvider().GetPrecompiles())
        {
            [IdentityPrecompile.Address] = new(precompile)
        };
        IPrecompileProvider provider = new FixedPrecompileProvider(precompiles.ToFrozenDictionary());
        _taikoMachine = new TaikoEthereumVirtualMachine(new TestBlockhashProvider(SpecProvider), SpecProvider, Substitute.For<IL1OriginStore>(), LimboLogs.Instance);
        _processor = new EthereumTransactionProcessor(BlobBaseFeeCalculator.Instance, SpecProvider, TestState, _taikoMachine,
            new CacheCodeInfoRepository(TestState, provider, new StaticCodeCache(16)), LimboLogs.Instance);
    }

    /// <returns>Whether the call to the precompile succeeded.</returns>
    private bool ExecuteCall()
    {
        byte[] bytecode = Prepare.EvmCode
            .Call(IdentityPrecompile.Address, CallGas)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .Done;

        (Block block, Transaction transaction) = PrepareTx(Activation, 2 * CallGas, bytecode);
        TransactionResult result = _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), NullTxTracer.Instance);
        Assert.That(result.TransactionExecuted, Is.True, result.ToString());

        TestState.Get(new StorageCell(Recipient, UInt256.Zero), out UInt256 callSucceeded);
        return !callSucceeded.IsZero;
    }

    private sealed class ThrowingPrecompile(Exception exception) : IContextAwarePrecompile
    {
        public ulong BaseGasCost(IReleaseSpec releaseSpec) => 50_000;

        public ulong DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => 0;

        public Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => throw exception;

        public Result<(byte[] returnValue, ulong gasConsumed)> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec, in PrecompileExtras extras) =>
            throw exception;
    }

    private sealed class FixedPrecompileProvider(FrozenDictionary<AddressAsKey, CodeInfo> precompiles) : IPrecompileProvider
    {
        public FrozenDictionary<AddressAsKey, CodeInfo> GetPrecompiles() => precompiles;
    }
}

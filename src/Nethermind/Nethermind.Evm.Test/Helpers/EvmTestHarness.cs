// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Threading;
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;

#nullable enable

namespace Nethermind.Evm.Test.Helpers;

/// <summary>
/// Bundle of the standard pieces a metrics/slow-block integration test needs to drive a
/// transaction through <see cref="EthereumTransactionProcessor"/>: spec provider, world state +
/// pre-genesis scope, tx processor, and an ECDSA signer.
/// </summary>
/// <remarks>
/// Disposing this instance releases the world-state scope and resets
/// <see cref="ProcessingThread.IsBlockProcessingThread"/> to its previous value, so the
/// fixture's <c>[TearDown]</c> can be a single <c>_harness.Dispose()</c> call.
/// </remarks>
public sealed class EvmTestHarness : IDisposable
{
    public ISpecProvider SpecProvider { get; }
    public IWorldState WorldState { get; }
    public ITransactionProcessor TxProcessor { get; }
    public IEthereumEcdsa Ecdsa { get; }

    private readonly IDisposable _scope;
    private readonly bool _previousIsBlockProcessingThread;
    private readonly IReleaseSpec _spec;

    /// <summary>
    /// Builds the harness and (when <paramref name="markBlockProcessingThread"/> is <c>true</c>,
    /// the default) sets <see cref="ProcessingThread.IsBlockProcessingThread"/> so EVM
    /// <c>Increment*</c> calls route to the <c>_main</c> counters — letting tests assert against
    /// <c>MainThread*</c> totals without cross-fixture contamination from background threads.
    /// </summary>
    public EvmTestHarness(IReleaseSpec? spec = null, bool markBlockProcessingThread = true)
    {
        _spec = spec ?? Prague.Instance;
        _previousIsBlockProcessingThread = ProcessingThread.IsBlockProcessingThread;
        if (markBlockProcessingThread) ProcessingThread.IsBlockProcessingThread = true;

        SpecProvider = new TestSpecProvider(_spec);
        WorldState = TestWorldStateFactory.CreateForTest();
        _scope = WorldState.BeginScope(IWorldState.PreGenesis);
        EthereumCodeInfoRepository codeInfoRepo = new(WorldState);
        EthereumVirtualMachine vm = new(new TestBlockhashProvider(SpecProvider), SpecProvider, LimboLogs.Instance);
        TxProcessor = new EthereumTransactionProcessor(BlobBaseFeeCalculator.Instance, SpecProvider, WorldState, vm, codeInfoRepo, LimboLogs.Instance);
        Ecdsa = new EthereumEcdsa(SpecProvider.ChainId);
    }

    /// <summary>Builds a Prague-timestamped block with the given transactions and a 30M gas limit.</summary>
    public Block CreateBlock(params Transaction[] txs) =>
        Build.A.Block.WithNumber(long.MaxValue).WithTimestamp(MainnetSpecProvider.PragueBlockTimestamp)
            .WithTransactions(txs).WithGasLimit(30_000_000).TestObject;

    /// <summary>Deploys <paramref name="code"/> at <paramref name="address"/> for SLOAD/SSTORE/CALL tests.</summary>
    public void DeployCode(Address address, byte[] code)
    {
        WorldState.CreateAccountIfNotExists(address, 0);
        WorldState.InsertCode(address, code, _spec);
    }

    /// <summary>Runs <paramref name="tx"/> against <paramref name="block"/>'s header using the harness's tx processor.</summary>
    public void ExecuteTx(Transaction tx, Block block, ITxTracer? tracer = null) =>
        TxProcessor.Execute(tx, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer ?? NullTxTracer.Instance);

    public void Dispose()
    {
        _scope.Dispose();
        ProcessingThread.IsBlockProcessingThread = _previousIsBlockProcessingThread;
    }
}

// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Receipts;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.State.OverridableEnv;
using Nethermind.TxPool;
using TxEventArgs = Nethermind.TxPool.TxEventArgs;

namespace Nethermind.FFI;

/// <summary>Node operations exposed to the native host.</summary>
public sealed unsafe class FfiNode(
    IBlockTree blockTree,
    IStateReader stateReader,
    ISpecProvider specProvider,
    IReadOnlyList<IBlockPreprocessorStep> preprocessorSteps,
    BlockValidator blockValidator,
    RegeneratingReceiptsEnvSourceFactory envSourceFactory,
    ITxPool txPool,
    ILogManager logManager) : IDisposable
{
    // NoValidation keeps the block processor's own validator out; the processed block is validated explicitly below.
    private const ProcessingOptions Options = ProcessingOptions.ReadOnlyChain
        | ProcessingOptions.ForceProcessing
        | ProcessingOptions.NoValidation
        | ProcessingOptions.ForceSequentialBlockAccessList;

    private static readonly BlockDecoder BlockDecoder = new();

    private readonly IShareableOverridableEnvSource<ReceiptsRegenerationEnv> _envSource =
        envSourceFactory.Create(Environment.ProcessorCount);

    private readonly ILogger _logger = logManager.GetClassLogger<FfiNode>();
    private TxCallback? _txCallback;
    private int _txPoolSubscribed;

    public BlockHeader? Head => blockTree.Head?.Header;

    /// <summary>Opens a child scope of the started runner's container that provides the <see cref="FfiNode"/>.</summary>
    public static ILifetimeScope CreateScope(ILifetimeScope runnerScope) => runnerScope.BeginLifetimeScope(static builder => builder
        // Registered as its own type, not IBlockValidator: the latter is decorated by the merge plugin's
        // InvalidBlockInterceptor, which would record blocks submitted over FFI into the process-wide
        // InvalidChainTracker and could make the Engine API reject them.
        .AddSingleton<BlockValidator>()
        .AddSingleton<RegeneratingReceiptsEnvSourceFactory>()
        .AddSingleton<FfiNode>());

    /// <summary>
    /// Validates and executes <paramref name="blockRlp"/> on top of its parent's state, discarding the resulting state.
    /// </summary>
    /// <remarks>
    /// Runs the same checks as importing the block: suggested-block validation against the parent before execution,
    /// and processed-block validation (state root, receipts root, gas used, bloom) after it.
    /// </remarks>
    public BlockExecutionResult ExecuteBlock(ReadOnlySpan<byte> blockRlp)
    {
        Block block;
        try
        {
            block = BlockDecoder.DecodeCompleteNotNull(blockRlp);
        }
        catch (RlpException e)
        {
            return new(FfiStatus.DecodeError, e.Message);
        }

        BlockHeader? parent = blockTree.FindParentHeader(block.Header, BlockTreeLookupOptions.DoNotCreateLevelIfMissing);
        if (parent is null) return new(FfiStatus.ParentNotFound, $"Parent {block.ParentHash} of block {block.Number} not found");
        if (!stateReader.HasStateForBlock(parent)) return new(FfiStatus.StateUnavailable, $"No state for parent {parent.ToString(BlockHeader.Format.Short)}");

        foreach (IBlockPreprocessorStep step in preprocessorSteps) step.RecoverData(block);

        if (!blockValidator.ValidateSuggestedBlock(block, parent, out string? error)) return new(FfiStatus.InvalidBlock, error);

        if (!_envSource.TryBuildAndOverrideAtTarget(block.Header, stateOverride: null, out Scope<ReceiptsRegenerationEnv>? scope))
            return new(FfiStatus.StateUnavailable, $"No state for parent {parent.ToString(BlockHeader.Format.Short)}");

        using IDisposable _ = scope;
        try
        {
            IReleaseSpec spec = specProvider.GetSpec(block.Header);
            (Block processed, TxReceipt[] receipts) = scope.Component.BlockProcessor
                .ProcessOne(block, Options, NullBlockTracer.Instance, spec, CancellationToken.None);

            return blockValidator.ValidateProcessedBlock(processed, receipts, block, out error)
                ? new(FfiStatus.Ok, null, processed.Header, EncodeReceipts(receipts, spec))
                : new(FfiStatus.InvalidBlock, error);
        }
        catch (InvalidBlockException e)
        {
            return new(FfiStatus.InvalidBlock, e.Message);
        }
        finally
        {
            block.DisposeAccountChanges();
        }
    }

    /// <summary>Sets the native callback receiving tx pool events, or clears it with a null <paramref name="callback"/>.</summary>
    /// <remarks>
    /// Invoked synchronously on the thread raising the pool event, possibly concurrently. The pool is only subscribed
    /// to once a callback is first set, so a host that never sets one adds nothing to tx admission.
    /// </remarks>
    /// <exception cref="InvalidOperationException">A callback is already set; it has to be cleared first.</exception>
    public void SetTxCallback(delegate* unmanaged[Cdecl]<nint, int, byte*, byte*, nuint, void> callback, nint userData)
    {
        if (callback is null)
        {
            Volatile.Write(ref _txCallback, null);
            return;
        }

        if (Interlocked.CompareExchange(ref _txCallback, new TxCallback(callback, userData), null) is not null)
            throw new InvalidOperationException("A tx callback is already set.");

        if (Interlocked.Exchange(ref _txPoolSubscribed, 1) != 0) return;

        txPool.NewPending += OnNewPending;
        txPool.RemovedPending += OnRemovedPending;
        txPool.EvictedPending += OnEvictedPending;
    }

    public void Dispose()
    {
        if (Volatile.Read(ref _txPoolSubscribed) != 0)
        {
            txPool.NewPending -= OnNewPending;
            txPool.RemovedPending -= OnRemovedPending;
            txPool.EvictedPending -= OnEvictedPending;
        }

        _envSource.Dispose();
    }

    private void OnNewPending(object? sender, TxEventArgs e) => Notify(FfiTxEvent.Pending, e.Transaction);

    private void OnRemovedPending(object? sender, TxEventArgs e) => Notify(FfiTxEvent.Removed, e.Transaction);

    private void OnEvictedPending(object? sender, TxEventArgs e) => Notify(FfiTxEvent.Evicted, e.Transaction);

    private void Notify(FfiTxEvent txEvent, Transaction tx)
    {
        if (Volatile.Read(ref _txCallback) is not { } callback) return;

        // Raised from inside the pool: an exception here would fail the tx admission or removal that raised it.
        try
        {
            using ArrayPoolList<byte> encoded = TxDecoder.Instance.EncodeToArrayPoolList(tx, RlpBehaviors.SkipTypedWrapping);
            fixed (byte* hash = tx.Hash!.Bytes)
            fixed (byte* encodedTx = encoded.AsSpan())
            {
                callback.Function(callback.UserData, (int)txEvent, hash, encodedTx, (nuint)encoded.Count);
            }
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error($"FFI tx callback failed for {txEvent} {tx.Hash}", e);
        }
    }

    private static byte[] EncodeReceipts(TxReceipt[] receipts, IReleaseSpec spec) =>
        Rlp.Encode(receipts, spec.IsEip658Enabled ? RlpBehaviors.Eip658Receipts : RlpBehaviors.None).Bytes;

    private sealed class TxCallback(delegate* unmanaged[Cdecl]<nint, int, byte*, byte*, nuint, void> function, nint userData)
    {
        public readonly delegate* unmanaged[Cdecl]<nint, int, byte*, byte*, nuint, void> Function = function;
        public readonly nint UserData = userData;
    }
}

/// <param name="Header">The processed header carrying the computed roots, when <paramref name="Status"/> is <see cref="FfiStatus.Ok"/>.</param>
/// <param name="ReceiptsRlp">The receipts as an RLP list in eth wire form.</param>
public readonly record struct BlockExecutionResult(FfiStatus Status, string? Error, BlockHeader? Header = null, byte[]? ReceiptsRlp = null);

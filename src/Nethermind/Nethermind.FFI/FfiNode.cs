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
using Nethermind.Core.Exceptions;
using Nethermind.Core.Specs;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.State.OverridableEnv;

namespace Nethermind.FFI;

/// <summary>Node operations exposed to the native host.</summary>
public sealed class FfiNode(
    IBlockTree blockTree,
    IStateReader stateReader,
    ISpecProvider specProvider,
    IReadOnlyList<IBlockPreprocessorStep> preprocessorSteps,
    BlockValidator blockValidator,
    RegeneratingReceiptsEnvSourceFactory envSourceFactory) : IDisposable
{
    // NoValidation keeps the block processor's own validator out; the processed block is validated explicitly below.
    private const ProcessingOptions Options = ProcessingOptions.ReadOnlyChain
        | ProcessingOptions.ForceProcessing
        | ProcessingOptions.NoValidation
        | ProcessingOptions.ForceSequentialBlockAccessList;

    private static readonly BlockDecoder BlockDecoder = new();

    private readonly IShareableOverridableEnvSource<ReceiptsRegenerationEnv> _envSource =
        envSourceFactory.Create(Environment.ProcessorCount);

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

    public void Dispose() => _envSource.Dispose();

    private static byte[] EncodeReceipts(TxReceipt[] receipts, IReleaseSpec spec) =>
        Rlp.Encode(receipts, spec.IsEip658Enabled ? RlpBehaviors.Eip658Receipts : RlpBehaviors.None).Bytes;
}

/// <param name="Header">The processed header carrying the computed roots, when <paramref name="Status"/> is <see cref="FfiStatus.Ok"/>.</param>
/// <param name="ReceiptsRlp">The receipts as an RLP list in eth wire form.</param>
public readonly record struct BlockExecutionResult(FfiStatus Status, string? Error, BlockHeader? Header = null, byte[]? ReceiptsRlp = null);

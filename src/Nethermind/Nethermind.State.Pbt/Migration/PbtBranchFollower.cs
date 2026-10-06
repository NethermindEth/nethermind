// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.Eip7928;

namespace Nethermind.State.Pbt.Migration;

/// <summary>Brings PBT up to every pre-activation block main processing executes, on any branch, from the stored BALs.</summary>
/// <remarks>
/// Main processing runs on flat alone before activation and must not wait for PBT, so each processed block only
/// becomes a target here and is replayed in the background. A target is replayed from the closest ancestor PBT
/// holds, whether or not its branch is canonical, so the activation block of any branch finds its parent in PBT.
/// A branch is dropped only once a block beside it is finalized. A target whose ancestry PBT does not hold yet
/// (the anchor import is still running) or whose replay failed is retried, and failures are logged. Until PBT holds
/// the activation parent, processing the activation block fails without marking it invalid.
/// </remarks>
internal sealed class PbtBranchFollower(
    IBlockTree blockTree,
    IBlockAccessListStore balStore,
    IPbtDbManager manager,
    PbtBalReplay replay,
    ISpecProvider specProvider,
    IMainProcessingContext mainProcessingContext,
    ILogManager logManager) : IAsyncDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    private readonly ILogger _logger = logManager.GetClassLogger<PbtBranchFollower>();
    private readonly object _gate = new();
    private readonly Dictionary<Hash256AsKey, BlockHeader> _targets = [];
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _cancellation = new();
    private Task _worker = Task.CompletedTask;
    private bool _started;
    private bool _disposed;
    private string? _lastError;

    public void Start()
    {
        lock (_gate)
        {
            if (_started || _disposed) return;
            _started = true;
            mainProcessingContext.BranchProcessor.BlockProcessed += OnBlockProcessed;
            _worker = Task.Run(Run);
        }
    }

    private void OnBlockProcessed(object? sender, BlockProcessedEventArgs args) => Add(args.Block.Header);

    /// <summary>Queues <paramref name="header"/> for replay; a pre-activation block only, since PBT executes the rest itself.</summary>
    public void Add(BlockHeader header)
    {
        if (header.Hash is null || specProvider.GetSpec(header).IsEip8347Enabled) return;
        lock (_gate)
        {
            // Replaying the child covers its parent.
            if (header.ParentHash is not null) _targets.Remove(header.ParentHash);
            _targets[header.Hash] = header;
        }
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    private async Task Run()
    {
        CancellationToken token = _cancellation.Token;
        try
        {
            while (true)
            {
                BlockHeader[] targets;
                lock (_gate) targets = [.. _targets.Values];
                bool pending = false;
                foreach (BlockHeader target in targets)
                {
                    if (TryFollow(target, token))
                    {
                        lock (_gate) _targets.Remove(target.Hash!);
                    }
                    else
                    {
                        pending = true;
                    }
                }
                await _wake.WaitAsync(pending ? RetryDelay : Timeout.InfiniteTimeSpan, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private bool TryFollow(BlockHeader target, CancellationToken token)
    {
        try
        {
            return Follow(target, token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (exception.Message != _lastError && _logger.IsWarn)
                _logger.Warn($"PBT BAL replay towards {target.ToString(BlockHeader.Format.Short)} failed; retrying: {exception.Message}");
            _lastError = exception.Message;
            return false;
        }
    }

    /// <returns>True once PBT holds <paramref name="target"/> or its branch is dead; false while PBT holds no ancestor of it.</returns>
    internal bool Follow(BlockHeader target, CancellationToken token)
    {
        BlockHeader? finalized = blockTree.FinalizedHash is { } finalizedHash && finalizedHash != Hash256.Zero
            ? blockTree.FindHeader(finalizedHash, BlockTreeLookupOptions.None)
            : null;
        using ArrayPoolList<BlockHeader> path = new(1);
        BlockHeader cursor = target;
        while (!manager.HasStateForBlock(new StateId(cursor)))
        {
            token.ThrowIfCancellationRequested();
            if (IsBesideFinalized(cursor, finalized)) return true;
            if (cursor.IsGenesis) return false;
            path.Add(cursor);
            cursor = blockTree.FindHeader(cursor.ParentHash!, BlockTreeLookupOptions.None)
                ?? throw new InvalidOperationException($"The parent of {cursor.ToString(BlockHeader.Format.Short)} is unavailable.");
        }
        for (int index = path.Count - 1; index >= 0; index--)
        {
            token.ThrowIfCancellationRequested();
            BlockHeader child = path[index];
            if (!manager.HasStateForBlock(new StateId(child))) Replay(cursor, child);
            cursor = child;
        }
        return true;
    }

    /// <summary>Whether the finalized chain holds another block at the height of <paramref name="header"/>.</summary>
    private bool IsBesideFinalized(BlockHeader header, BlockHeader? finalized) =>
        finalized is not null && header.Number <= finalized.Number
        && blockTree.FindHeader(header.Number, BlockTreeLookupOptions.RequireCanonical)?.Hash != header.Hash;

    private void Replay(BlockHeader parent, BlockHeader child)
    {
        using MemoryManager<byte>? owner = balStore.GetRlp(child.Number, child.Hash!);
        if (owner is null || child.BlockAccessListHash is null || ValueKeccak.Compute(owner.Memory.Span) != child.BlockAccessListHash.ValueHash256)
            throw new InvalidDataException($"No BAL matching the header of {child.ToString(BlockHeader.Format.Short)} is stored.");
        RlpReader reader = new(owner.Memory.Span);
        replay.Apply(parent, child, BlockAccessListDecoder.Instance.Decode(ref reader)
            ?? throw new InvalidDataException($"The BAL of {child.ToString(BlockHeader.Format.Short)} is empty."));
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_started) mainProcessingContext.BranchProcessor.BlockProcessed -= OnBlockProcessed;
        }
        _cancellation.Cancel();
        await _worker;
        _cancellation.Dispose();
    }
}

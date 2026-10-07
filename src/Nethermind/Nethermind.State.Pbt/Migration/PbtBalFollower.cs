// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Logging;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.Eip7928;
using Nethermind.Synchronization.FastSync;

namespace Nethermind.State.Pbt.Migration;

/// <summary>The canonical block the follower has brought PBT up to, with the EIP-8297 root PBT computed for it.</summary>
internal sealed record PbtFollowerCursor(ulong Number, Hash256 Hash, Hash256 TreeRoot)
{
    /// <summary>The cursor at <paramref name="header"/> with the root PBT holds for it, or the zero root when it holds none.</summary>
    public static PbtFollowerCursor At(IPbtDbManager manager, BlockHeader header)
    {
        using PbtReadOnlySnapshotBundle? bundle = manager.TryGatherReadOnlyBundle(new StateId(header));
        return new(header.Number, header.Hash!, (bundle?.TreeRoot ?? default).ToHash256());
    }
}

/// <summary>Advances the native PBT state through authenticated canonical BALs, without executing transactions.</summary>
/// <remarks>
/// Brings PBT from the imported anchor up to the canonical head, fetching missing BALs from peers.
/// <see cref="PbtBranchFollower"/> replays the blocks main processing executes, on any branch.
/// A reorg needs no rewind: PBT retains every state above its persisted pointer, so the walk from the target simply
/// lands on the canonical ancestor it still holds. Past activation the header commits to the PBT root, so a replayed
/// block is also checked against it.
/// </remarks>
internal sealed class PbtBalFollower(
    IBlockTree blockTree,
    BalFetcher fetcher,
    IBlockAccessListStore balStore,
    IPbtDbManager manager,
    PbtBalReplay replay,
    ISpecProvider specProvider,
    ILogManager logManager)
{
    private readonly SemaphoreSlim _run = new(1, 1);
    private string? _error;
    private PbtFollowerCursor? _cursor;

    public string? Error => Volatile.Read(ref _error);
    public PbtFollowerCursor? Cursor => Volatile.Read(ref _cursor);

    public async Task<bool> Follow(BlockHeader target, CancellationToken cancellationToken)
    {
        await _run.WaitAsync(cancellationToken);
        try
        {
            Volatile.Write(ref _error, null);
            if (target.Hash is null) return Stall("Replay target has no block hash.");
            BlockHeader cursor = target;
            while (!manager.HasStateForBlock(new StateId(cursor)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (cursor.IsGenesis) return Stall("PBT holds no canonical ancestor of the target; re-import the migration anchor.");
                cursor = blockTree.FindHeader(cursor.ParentHash!, BlockTreeLookupOptions.RequireCanonical)
                    ?? throw new InvalidOperationException("Canonical ancestry is unavailable.");
                if (!blockTree.IsMainChain(target)) return Stall("Replay target is no longer canonical.");
            }
            Publish(cursor);
            while (cursor.Number < target.Number)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!blockTree.IsMainChain(target)) return Stall("Replay target is no longer canonical.");
                BlockHeader? child = blockTree.FindHeader(cursor.Number + 1, BlockTreeLookupOptions.RequireCanonical);
                if (child?.Hash is null || child.ParentHash != cursor.Hash) return Stall("Replay headers are unavailable or disconnected.");
                if (!manager.HasStateForBlock(new StateId(child)))
                {
                    BlockHeader parent = cursor;
                    if (!await AcquireBal(parent, child, bal => replay.Apply(parent, child, bal), cancellationToken))
                        return Stall("BAL range unavailable or canonical ancestry changed.");
                }
                cursor = child;
                Publish(cursor);
                if (specProvider.GetSpec(child).IsEip8347Enabled && Cursor!.TreeRoot != child.StateRoot)
                    return Stall($"BAL replay of {child.ToString(BlockHeader.Format.Short)} produced PBT root {Cursor.TreeRoot}, header commits to {child.StateRoot}.");
            }
            return blockTree.IsMainChain(target);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Volatile.Write(ref _error, exception.Message);
            throw;
        }
        finally
        {
            _run.Release();
        }
    }

    private void Publish(BlockHeader header) => Volatile.Write(ref _cursor, PbtFollowerCursor.At(manager, header));

    private const int MaxNoProgressRounds = 50;
    private readonly ILogger _logger = logManager.GetClassLogger<PbtBalFollower>();

    internal TimeSpan MigrationRetryDelay { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Authenticates the BAL of <paramref name="child"/> on its captured canonical ancestry and hands it to <paramref name="apply"/>.</summary>
    /// <remarks>
    /// <paramref name="apply"/> may run before a reorg makes this return false; it must not publish a migration cursor.
    /// The caller must recheck its own canonical epoch and the captured endpoint hashes atomically with manifest
    /// publication, even when this returns true.
    /// </remarks>
    /// <returns>True once <paramref name="apply"/> has run; false if ancestry changed, the headers are disconnected, or peers did not fill the gap.</returns>
    internal async Task<bool> AcquireBal(
        BlockHeader parent,
        BlockHeader child,
        Action<ReadOnlyBlockAccessList> apply,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (parent.Hash is null || child.Hash is null || child.ParentHash != parent.Hash || child.BlockAccessListHash is null) return false;

        using CancellationTokenSource fetchCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        object cancellationGate = new();
        bool subscribed = true;
        int changed = 0;
        void OnChainChanged(object? sender, OnUpdateMainChainArgs args)
        {
            foreach (BlockHeader header in args.Headers)
            {
                if (header.Number <= child.Number)
                {
                    lock (cancellationGate)
                    {
                        if (!subscribed) return;
                        Interlocked.Exchange(ref changed, 1);
                        fetchCancellation.Cancel();
                    }
                    break;
                }
            }
        }
        blockTree.OnUpdateMainChain += OnChainChanged;
        try
        {
            bool IsCurrent() => Volatile.Read(ref changed) == 0 && blockTree.IsMainChain(parent) && blockTree.IsMainChain(child);

            int attempts = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (!IsCurrent()) return false;
                using (MemoryManager<byte>? owner = balStore.GetRlp(child.Number, child.Hash))
                {
                    if (owner is not null && TryDecode(child, owner.Memory.Span) is { } bal)
                    {
                        token.ThrowIfCancellationRequested();
                        if (!IsCurrent()) return false;
                        apply(bal);
                        token.ThrowIfCancellationRequested();
                        return IsCurrent();
                    }
                }

                if (!IsCurrent()) return false;
                balStore.Delete(child.Number, child.Hash);
                if (attempts++ >= MaxNoProgressRounds) return false;
                try
                {
                    if (!await fetcher.EnsureRange(parent, child, fetchCancellation.Token)) return false;
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested && !IsCurrent())
                {
                    return false;
                }
                if (!IsCurrent()) return false;
                if (attempts > 1) await Task.Delay(MigrationRetryDelay, token);
            }
        }
        finally
        {
            lock (cancellationGate)
            {
                blockTree.OnUpdateMainChain -= OnChainChanged;
                subscribed = false;
            }
        }
    }

    /// <summary>Decodes <paramref name="rlp"/> when it is exactly the BAL <paramref name="header"/> commits to.</summary>
    /// <returns>The BAL, or null when <paramref name="rlp"/> encodes another one.</returns>
    /// <exception cref="RlpException"><paramref name="rlp"/> is not a single well-formed BAL.</exception>
    internal static ReadOnlyBlockAccessList? DecodeAuthenticated(BlockHeader header, ReadOnlySpan<byte> rlp)
    {
        RlpReader reader = new(rlp);
        ReadOnlyBlockAccessList? bal = BlockAccessListDecoder.Instance.Decode(ref reader);
        reader.Check(rlp.Length);
        // The decoder hashes the bytes it consumed, which the check pins to the whole input.
        return header.BlockAccessListHash is not null && bal?.WireHash == header.BlockAccessListHash ? bal : null;
    }

    private ReadOnlyBlockAccessList? TryDecode(BlockHeader header, ReadOnlySpan<byte> rlp)
    {
        try
        {
            return DecodeAuthenticated(header, rlp);
        }
        catch (RlpException exception)
        {
            if (_logger.IsDebug) _logger.Debug($"Invalid migration BAL for {header.Hash}: {exception.Message}");
            return null;
        }
    }

    private bool Stall(string error)
    {
        Volatile.Write(ref _error, error);
        return false;
    }
}

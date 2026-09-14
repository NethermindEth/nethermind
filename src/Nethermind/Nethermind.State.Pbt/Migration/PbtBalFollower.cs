// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Logging;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.Eip7928;
using Nethermind.Synchronization.FastSync;

namespace Nethermind.State.Pbt.Migration;

/// <summary>The canonical block the follower has brought PBT up to, with the EIP-8297 root PBT computed for it.</summary>
internal sealed record PbtFollowerCursor(ulong Number, Hash256 Hash, Hash256 TreeRoot);

/// <summary>Advances the native PBT state through authenticated canonical BALs, without executing transactions.</summary>
/// <remarks>
/// Needed only while PBT lags flat: after an anchor import behind the head, or when PBT persisted less than flat
/// before a crash. Main processing mirrors every block it executes, so once PBT holds the head the follower idles.
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
                if (blockTree.FindHeader(target.Number)?.Hash != target.Hash) return Stall("Replay target is no longer canonical.");
            }
            Publish(cursor);
            while (cursor.Number < target.Number)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (blockTree.FindHeader(target.Number)?.Hash != target.Hash) return Stall("Replay target is no longer canonical.");
                BlockHeader? child = blockTree.FindHeader(cursor.Number + 1, BlockTreeLookupOptions.RequireCanonical);
                if (child?.Hash is null || child.ParentHash != cursor.Hash) return Stall("Replay headers are unavailable or disconnected.");
                if (!manager.HasStateForBlock(new StateId(child)))
                {
                    BlockHeader parent = cursor;
                    bool consumed = false;
                    bool acquired = await AcquireRange(parent, child, (header, bytes, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        if (consumed || header.Hash != child.Hash || header.ParentHash != parent.Hash)
                            throw new InvalidDataException("Unexpected replay acquisition identity.");
                        RlpReader reader = new(bytes.Span);
                        replay.Apply(parent, child, BlockAccessListDecoder.Instance.Decode(ref reader)
                            ?? throw new InvalidDataException("Missing replay BAL."));
                        consumed = true;
                        return Task.CompletedTask;
                    }, cancellationToken);
                    if (!acquired || !consumed) return Stall("BAL range unavailable or canonical ancestry changed.");
                }
                cursor = child;
                Publish(cursor);
                if (specProvider.GetSpec(child).IsEip8347Enabled && Cursor!.TreeRoot != child.StateRoot)
                    return Stall($"BAL replay of {child.ToString(BlockHeader.Format.Short)} produced PBT root {Cursor.TreeRoot}, header commits to {child.StateRoot}.");
            }
            return blockTree.FindHeader(target.Number)?.Hash == target.Hash;
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

    private void Publish(BlockHeader header)
    {
        using PbtReadOnlySnapshotBundle? bundle = manager.TryGatherReadOnlyBundle(new StateId(header));
        Volatile.Write(ref _cursor, new PbtFollowerCursor(header.Number, header.Hash!, (bundle?.TreeRoot ?? default).ToHash256()));
    }

    private const int MigrationWindowSize = 128;
    private const int MaxNoProgressRounds = 50;
    private readonly ILogger _logger = logManager.GetClassLogger<PbtBalFollower>();

    internal TimeSpan MigrationRetryDelay { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Authenticates and lends the BALs after <paramref name="from"/> through <paramref name="to"/> on their captured canonical ancestry.</summary>
    /// <remarks>
    /// The consumer must copy or durably stage any required replay input before its awaited callback returns:
    /// the memory is borrowed and the store may prune it afterwards. Callbacks may run before a later gap or
    /// reorg returns false; they must not publish a migration cursor. The caller must recheck its own canonical
    /// epoch and the captured endpoint hashes atomically with manifest publication, even when this returns true.
    /// Only one BAL is fetched/decoded/lent at a time, including a legal BAL exceeding the normal 64 MiB budget.
    /// </remarks>
    /// <returns>False if ancestry changed, a header is unavailable, or peers did not fill a gap.</returns>
    internal async Task<bool> AcquireRange(
        BlockHeader from,
        BlockHeader to,
        Func<BlockHeader, ReadOnlyMemory<byte>, CancellationToken, Task> consume,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(consume);
        token.ThrowIfCancellationRequested();
        if (from.Hash is null || to.Hash is null || from.Number > to.Number) return false;

        using CancellationTokenSource fetchCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        object cancellationGate = new();
        bool subscribed = true;
        int changed = 0;
        void OnChainChanged(object? sender, OnUpdateMainChainArgs args)
        {
            foreach (BlockHeader header in args.Headers)
            {
                if (header.Number <= to.Number)
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
            Hash256 fromHash = from.Hash;
            Hash256 toHash = to.Hash;
            bool IsCurrent() => Volatile.Read(ref changed) == 0
                && blockTree.FindHeader(from.Number)?.Hash == fromHash
                && blockTree.FindHeader(to.Number)?.Hash == toHash;

            if (!IsCurrent()) return false;
            ulong cursor = from.Number;
            Hash256 parentHash = fromHash;
            while (cursor < to.Number)
            {
                int count = (int)Math.Min((ulong)MigrationWindowSize, to.Number - cursor);
                using ArrayPoolList<BlockHeader> headers = new(count);
                for (int index = 0; index < count; index++)
                {
                    token.ThrowIfCancellationRequested();
                    BlockHeader? header = blockTree.FindHeader(cursor + (ulong)index + 1);
                    if (header?.Hash is null || header.ParentHash != parentHash || header.BlockAccessListHash is null)
                        return false;
                    headers.Add(header);
                    parentHash = header.Hash;
                }
                if (!IsCurrent()) return false;

                foreach (BlockHeader header in headers)
                {
                    int attempts = 0;
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        if (!IsCurrent() || blockTree.FindHeader(header.Number)?.Hash != header.Hash) return false;
                        using (MemoryManager<byte>? owner = balStore.GetRlp(header.Number, header.Hash!))
                        {
                            if (owner is not null && ValidateBal(header, owner.Memory.Span))
                            {
                                token.ThrowIfCancellationRequested();
                                if (!IsCurrent() || blockTree.FindHeader(header.Number)?.Hash != header.Hash) return false;
                                await consume(header, owner.Memory, token);
                                token.ThrowIfCancellationRequested();
                                if (!IsCurrent() || blockTree.FindHeader(header.Number)?.Hash != header.Hash) return false;
                                break;
                            }
                        }

                        if (!IsCurrent()) return false;
                        balStore.Delete(header.Number, header.Hash!);
                        if (attempts++ >= MaxNoProgressRounds) return false;
                        BlockHeader? parent = blockTree.FindHeader(header.Number - 1);
                        if (parent is null || parent.Hash != header.ParentHash) return false;
                        try
                        {
                            if (!await fetcher.EnsureRange(parent, header, fetchCancellation.Token)) return false;
                        }
                        catch (OperationCanceledException) when (!token.IsCancellationRequested && !IsCurrent())
                        {
                            return false;
                        }
                        if (!IsCurrent()) return false;
                        if (attempts > 1) await Task.Delay(MigrationRetryDelay, token);
                    }
                }
                cursor += (ulong)count;
            }
            return parentHash == toHash && IsCurrent();
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

    private bool ValidateBal(BlockHeader header, ReadOnlySpan<byte> rlp)
    {
        if (header.BlockAccessListHash is null || ValueKeccak.Compute(rlp) != header.BlockAccessListHash.ValueHash256) return false;
        try
        {
            RlpReader reader = new(rlp);
            BlockAccessListDecoder.Instance.Decode(ref reader);
            reader.Check(rlp.Length);
            return true;
        }
        catch (RlpException exception)
        {
            if (_logger.IsDebug) _logger.Debug($"Invalid migration BAL for {header.Hash}: {exception.Message}");
            return false;
        }
    }

    private bool Stall(string error)
    {
        Volatile.Write(ref _error, error);
        return false;
    }
}

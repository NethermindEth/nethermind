// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Logging;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.Eip7928;
using Nethermind.State.Flat;
using Nethermind.State.Pbt.Snapshot;
using Nethermind.Synchronization.FastSync;

namespace Nethermind.State.Pbt.Migration;

/// <summary>The canonical block the follower has brought PBT up to, with the EIP-8297 root PBT computed for it.</summary>
public sealed record PbtFollowerCursor(ulong Number, Hash256 Hash, Hash256 TreeRoot)
{
    /// <summary>The cursor at <paramref name="header"/> with the root PBT holds for it, or the zero root when it holds none.</summary>
    public static PbtFollowerCursor At(IPbtDbManager manager, BlockHeader header)
    {
        using PbtReadOnlySnapshotBundle? bundle = manager.TryGatherReadOnlyBundle(new StateId(header));
        return new(header.Number, header.Hash!, (bundle?.TreeRoot ?? default).ToHash256());
    }
}

/// <summary>Brings PBT up to every pre-activation block the node knows of, on any branch, through authenticated BALs, without executing transactions.</summary>
/// <remarks>
/// Main processing runs on flat alone before activation and must not wait for PBT, so every suggested block and the
/// canonical head become targets replayed in the background. A target is replayed from the closest ancestor PBT holds,
/// whether or not its branch is canonical, so the activation block of any branch finds its parent in PBT. A canonical
/// block's missing BAL is fetched from peers; a side branch's BAL must already be stored. A target is dropped only when
/// its ancestry is disconnected or a finalized block competes with it; anything else, including an ancestry PBT does not
/// hold yet while the anchor import runs, is retried. A reorg needs no rewind: PBT retains every state above its
/// persisted pointer. Until PBT holds the activation parent, processing the activation block fails without marking it
/// invalid.
/// </remarks>
public sealed class PbtBalFollower(
    IBlockTree blockTree,
    BalFetcher fetcher,
    IBlockAccessListStore balStore,
    IPbtDbManager manager,
    PbtPersistenceManager persistenceManager,
    PbtBalReplay replay,
    ISpecProvider specProvider,
    IFlatDbManager flatDbManager,
    ILogManager logManager) : IAsyncDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    private readonly SemaphoreSlim _run = new(1, 1);
    private readonly object _gate = new();
    private readonly Dictionary<Hash256AsKey, BlockHeader> _targets = [];
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _cancellation = new();
    private Task _worker = Task.CompletedTask;
    private bool _started;
    private bool _disposed;
    private bool _flatFlushed;
    private string? _lastLoggedError;
    private string? _error;
    private PbtFollowerCursor? _cursor;

    public string? Error => Volatile.Read(ref _error);

    /// <summary>The highest canonical block a replay has brought PBT up to.</summary>
    public PbtFollowerCursor? Cursor => Volatile.Read(ref _cursor);

    public void Start()
    {
        lock (_gate)
        {
            if (_started || _disposed) return;
            _started = true;
            blockTree.NewSuggestedBlock += OnNewSuggestedBlock;
            blockTree.OnUpdateMainChain += OnCanonicalChanged;
            blockTree.OnForkChoiceUpdated += OnForkChoiceUpdated;
            _worker = Task.Run(Run);
        }
        AddHead();
    }

    // A header-only suggestion carries no block.
    private void OnNewSuggestedBlock(object? sender, BlockEventArgs args)
    {
        if (args.Block is { } block) Add(block.Header);
    }

    private void OnCanonicalChanged(object? sender, OnUpdateMainChainArgs args) => AddHead();

    private void OnForkChoiceUpdated(object? sender, IBlockTree.ForkChoiceUpdateEventArgs args) => FlushFlatOnce();

    private void AddHead()
    {
        FlushFlatOnce();
        if (blockTree.Head is { } head) Add(head.Header);
    }

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
                    if (await TryFollow(target, token))
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

    private async Task<bool> TryFollow(BlockHeader target, CancellationToken token)
    {
        try
        {
            return await Follow(target, token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (exception.Message != _lastLoggedError && _logger.IsWarn)
                _logger.Warn($"PBT BAL replay towards {target.ToString(BlockHeader.Format.Short)} failed; retrying: {exception.Message}");
            _lastLoggedError = exception.Message;
            return false;
        }
    }

    /// <summary>Replays the BALs from the closest ancestor PBT holds up to <paramref name="target"/>.</summary>
    /// <returns>True once PBT holds <paramref name="target"/> or the target is dropped; false while it is to be retried.</returns>
    public async Task<bool> Follow(BlockHeader target, CancellationToken cancellationToken)
    {
        await _run.WaitAsync(cancellationToken);
        try
        {
            Volatile.Write(ref _error, null);
            if (target.Hash is null) return Stall("Replay target has no block hash.");
            BlockHeader? finalized = blockTree.FindFinalizedHeader();
            StateId persisted = persistenceManager.GetCurrentPersistedStateId();
            using ArrayPoolList<BlockHeader> path = new(1);
            BlockHeader cursor = target;
            while (!manager.HasStateForBlock(new StateId(cursor)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsBesideFinalized(cursor, finalized)) return true;
                // PBT holds no state below its persisted one, and none at all until the anchor is published.
                if (persisted == StateId.PreGenesis || cursor.Number <= persisted.BlockNumber) return Stall("PBT holds no ancestor of the target yet.");
                path.Add(cursor);
                BlockHeader? parent = blockTree.FindHeader(cursor.ParentHash!, BlockTreeLookupOptions.None);
                if (parent is null)
                {
                    if (_logger.IsDebug) _logger.Debug($"Dropping PBT BAL replay towards {target.ToString(BlockHeader.Format.Short)}: the parent of {cursor.ToString(BlockHeader.Format.Short)} is unavailable.");
                    return true;
                }
                cursor = parent;
            }
            Publish(cursor);
            for (int index = path.Count - 1; index >= 0; index--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                BlockHeader child = path[index];
                if (!manager.HasStateForBlock(new StateId(child)) && !await Replay(cursor, child, cancellationToken))
                    return Stall("BAL range unavailable or canonical ancestry changed.");
                cursor = child;
                Publish(cursor);
            }
            return true;
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

    /// <summary>Whether the finalized chain holds another block at the height of <paramref name="header"/>.</summary>
    private bool IsBesideFinalized(BlockHeader header, BlockHeader? finalized) =>
        finalized is not null && header.Number <= finalized.Number
        && !blockTree.IsMainChain(header);

    /// <returns>False when a canonical block's BAL could not be acquired or its ancestry changed meanwhile.</returns>
    private async Task<bool> Replay(BlockHeader parent, BlockHeader child, CancellationToken token)
    {
        if (blockTree.IsMainChain(child)) return await AcquireBal(parent, child, bal => replay.Apply(parent, child, bal), token);
        using MemoryManager<byte>? owner = balStore.GetRlp(child.Number, child.Hash!);
        replay.Apply(parent, child, (owner is null ? null : DecodeAuthenticated(child, owner.Memory.Span))
            ?? throw new InvalidDataException($"No BAL matching the header of {child.ToString(BlockHeader.Format.Short)} is stored."));
        return true;
    }

    // A side branch's replay lands on its canonical fork point, which must not pull the cursor back unless a reorg left the cursor off the main chain.
    private void Publish(BlockHeader header)
    {
        if (!blockTree.IsMainChain(header)) return;
        PbtFollowerCursor? current = Cursor;
        if (current is null || header.Number >= current.Number || !blockTree.IsMainChain(current.Hash, throwOnMissingHash: false))
            Volatile.Write(ref _cursor, PbtFollowerCursor.At(manager, header));
    }

    /// <remarks>
    /// Flat gets no commits after activation, so nothing nudges its persistence: persist its remaining
    /// pre-activation states once the activation is final (see <see cref="MigrationFlatFinalizedStateProvider"/>).
    /// </remarks>
    private void FlushFlatOnce()
    {
        lock (_gate)
        {
            if (_disposed || _flatFlushed || !MigrationActivation.IsFinal(blockTree, specProvider)) return;
            _flatFlushed = true;
        }
        Task.Run(() =>
        {
            try { flatDbManager.FlushCache(CancellationToken.None); }
            catch (Exception exception)
            {
                if (_logger.IsError) _logger.Error("Persisting the pre-activation flat state failed", exception);
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_started)
            {
                blockTree.NewSuggestedBlock -= OnNewSuggestedBlock;
                blockTree.OnUpdateMainChain -= OnCanonicalChanged;
                blockTree.OnForkChoiceUpdated -= OnForkChoiceUpdated;
            }
        }
        _cancellation.Cancel();
        await _worker;
        _cancellation.Dispose();
    }

    private const int MaxNoProgressRounds = 50;
    private readonly ILogger _logger = logManager.GetClassLogger<PbtBalFollower>();

    private static readonly TimeSpan MigrationRetryDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>Authenticates the BAL of <paramref name="child"/> on its captured canonical ancestry and hands it to <paramref name="apply"/>.</summary>
    /// <remarks>
    /// <paramref name="apply"/> may run before a reorg makes this return false; it must not publish a migration cursor.
    /// The caller must recheck its own canonical epoch and the captured endpoint hashes atomically with manifest
    /// publication, even when this returns true.
    /// </remarks>
    /// <returns>True once <paramref name="apply"/> has run; false if ancestry changed, the headers are disconnected, or peers did not fill the gap.</returns>
    public async Task<bool> AcquireBal(
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
    public static ReadOnlyBlockAccessList? DecodeAuthenticated(BlockHeader header, ReadOnlySpan<byte> rlp)
    {
        // The decoder hashes the bytes it consumed, which DecodeComplete pins to the whole input.
        ReadOnlyBlockAccessList? bal = BlockAccessListDecoder.Instance.DecodeComplete(rlp);
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

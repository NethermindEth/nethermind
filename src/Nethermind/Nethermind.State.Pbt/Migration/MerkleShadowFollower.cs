// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.Eip7928;
using Nethermind.State.Flat.ScopeProvider;

namespace Nethermind.State.Pbt.Migration;

/// <summary>The Merkle side of the migration after activation: the MPT roots computed for post-activation blocks.</summary>
internal interface IMerkleShadowFollower
{
    /// <summary>The canonical block the MPT has been brought up to, with its MPT root; null before the first replay.</summary>
    PbtFollowerCursor? Cursor { get; }
    string? Error { get; }

    /// <summary>The MPT root the follower computed for the block, or null when it has not replayed it.</summary>
    Hash256? GetShadowRoot(Hash256 blockHash);
}

/// <summary>Keeps the MPT current through the EIP-8347 transition window by replaying canonical BALs into flat.</summary>
/// <remarks>
/// EIP-8347 requires both trees until the activation is finalized. Main processing commits only to PBT after
/// activation, so this follower advances a private read-only flat scope from the last flat-held canonical
/// ancestor, one authenticated BAL at a time, without executing transactions. The replayed states stay inside
/// that scope: flat's repository never holds a post-activation state, so its persistence and the backend
/// selection are unaffected. A reorg or a stalled acquisition drops the scope and rebuilds from flat; a restart
/// rebuilds from flat's persisted state. Once the activation is final the scope is released with the MPT.
/// </remarks>
internal sealed class MerkleShadowFollower(
    IBlockTree blockTree,
    FlatWorldStateManager flat,
    ILifetimeScope rootLifetime,
    PbtBalFollower balAcquisition,
    ISpecProvider specProvider,
    ILogManager logManager) : IMerkleShadowFollower, IAsyncDisposable
{
    private readonly ILogger _logger = logManager.GetClassLogger<MerkleShadowFollower>();
    private readonly SemaphoreSlim _run = new(1, 1);
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<Hash256AsKey, Hash256> _shadowRoots = new();
    private PbtBalFollowerScheduler? _scheduler;
    private Task? _stopping;
    private ReplayState? _state;
    private string? _error;
    private PbtFollowerCursor? _cursor;

    public string? Error => Volatile.Read(ref _error);
    public PbtFollowerCursor? Cursor => Volatile.Read(ref _cursor);

    public Hash256? GetShadowRoot(Hash256 blockHash) => _shadowRoots.TryGetValue(blockHash, out Hash256? root) ? root : null;

    public void Start()
    {
        lock (_gate)
        {
            if (_scheduler is not null) return;
            _scheduler = new PbtBalFollowerScheduler(Follow, () => Error, () => Cursor);
            blockTree.OnUpdateMainChain += OnCanonicalChanged;
            blockTree.OnForkChoiceUpdated += OnForkChoiceUpdated;
        }
        Evaluate();
    }

    private void OnCanonicalChanged(object? sender, OnUpdateMainChainArgs args) => Evaluate();

    private void OnForkChoiceUpdated(object? sender, IBlockTree.ForkChoiceUpdateEventArgs args)
    {
        if (MigrationActivation.IsFinal(blockTree, specProvider)) _ = StopAsync();
    }

    private void Evaluate()
    {
        if (MigrationActivation.IsFinal(blockTree, specProvider))
        {
            _ = StopAsync();
            return;
        }
        PbtBalFollowerScheduler? scheduler;
        lock (_gate) scheduler = _stopping is null ? _scheduler : null;
        BlockHeader? head = blockTree.Head?.Header;
        if (scheduler is null || head is null || !specProvider.GetSpec(head).IsEip8347Enabled) return;
        scheduler.Schedule(head);
    }

    /// <summary>Stops following and releases the replay scope; the window closed or the node is shutting down.</summary>
    private Task StopAsync()
    {
        lock (_gate)
        {
            if (_stopping is not null) return _stopping;
            if (_scheduler is null) return Task.CompletedTask;
            blockTree.OnUpdateMainChain -= OnCanonicalChanged;
            blockTree.OnForkChoiceUpdated -= OnForkChoiceUpdated;
            return _stopping = StopCore(_scheduler);
        }
    }

    private async Task StopCore(PbtBalFollowerScheduler scheduler)
    {
        try
        {
            await scheduler.DisposeAsync();
            await _run.WaitAsync();
            try { Reset(); }
            finally { _run.Release(); }
        }
        catch (Exception exception)
        {
            if (_logger.IsError) _logger.Error("Releasing the Merkle shadow follower failed", exception);
        }
    }

    public async Task<bool> Follow(BlockHeader target, CancellationToken cancellationToken)
    {
        await _run.WaitAsync(cancellationToken);
        try
        {
            Volatile.Write(ref _error, null);
            if (target.Hash is null) return Stall("Replay target has no block hash.");
            ReplayState? replay = _state;
            if (replay is null || !IsCanonical(replay.Cursor) || !IsCanonical(target) || target.Number < replay.Cursor.Number)
            {
                Reset();
                BlockHeader anchor = target;
                while (!flat.GlobalStateReader.HasStateForBlock(anchor))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (anchor.IsGenesis) return Stall("Flat holds no canonical ancestor of the target.");
                    anchor = blockTree.FindHeader(anchor.ParentHash!, BlockTreeLookupOptions.RequireCanonical)
                        ?? throw new InvalidOperationException("Canonical ancestry is unavailable.");
                    if (!IsCanonical(target)) return Stall("Replay target is no longer canonical.");
                }
                replay = Open(anchor);
            }
            Publish(replay.Cursor, replay.WorldState.StateRoot);
            while (replay.Cursor.Number < target.Number)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsCanonical(target)) return Stall("Replay target is no longer canonical.");
                BlockHeader parent = replay.Cursor;
                BlockHeader? child = blockTree.FindHeader(parent.Number + 1, BlockTreeLookupOptions.RequireCanonical);
                if (child?.Hash is null || child.ParentHash != parent.Hash) return Stall("Replay headers are unavailable or disconnected.");
                bool consumed = false;
                bool acquired = await balAcquisition.AcquireRange(parent, child, (header, bytes, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    if (consumed || header.Hash != child.Hash || header.ParentHash != parent.Hash)
                        throw new InvalidDataException("Unexpected replay acquisition identity.");
                    RlpReader reader = new(bytes.Span);
                    MigrationBalStateChanges.Apply(BlockAccessListDecoder.Instance.Decode(ref reader) ?? throw new InvalidDataException("Missing replay BAL."),
                        replay.WorldState, specProvider.GetSpec(child));
                    replay.WorldState.CommitTree(child.Number);
                    // The scope now holds the child; a cancelled or failed acquisition must not leave the cursor behind it.
                    replay.Cursor = child;
                    consumed = true;
                    return Task.CompletedTask;
                }, cancellationToken);
                if (!acquired || !consumed) return Stall("BAL range unavailable or canonical ancestry changed.");
                Hash256 root = replay.WorldState.StateRoot;
                Publish(child, root);
                if (!specProvider.GetSpec(child).IsEip8347Enabled && root != child.StateRoot)
                    return Stall($"BAL replay of {child.ToString(BlockHeader.Format.Short)} produced MPT root {root}, header commits to {child.StateRoot}.");
            }
            return IsCanonical(target);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Reset();
            Volatile.Write(ref _error, exception.Message);
            throw;
        }
        finally
        {
            _run.Release();
        }
    }

    private bool IsCanonical(BlockHeader header) => blockTree.FindHeader(header.Number)?.Hash == header.Hash;

    private ReplayState Open(BlockHeader anchor)
    {
        ILifetimeScope lifetime = rootLifetime.BeginLifetimeScope(builder =>
            builder.RegisterInstance(flat.CreateResettableWorldState()).As<IWorldStateScopeProvider>());
        try
        {
            IWorldState worldState = lifetime.Resolve<IWorldState>();
            if (!worldState.TryBeginScope(anchor, out IDisposable? scopeCloser))
                throw new InvalidOperationException($"Flat state is unavailable at the shadow anchor {anchor.ToString(BlockHeader.Format.Short)}.");
            return _state = new ReplayState(lifetime, worldState, scopeCloser, anchor);
        }
        catch
        {
            lifetime.Dispose();
            throw;
        }
    }

    private void Reset()
    {
        ReplayState? state = _state;
        _state = null;
        if (state is null) return;
        try { state.Scope.Dispose(); }
        finally { state.Lifetime.Dispose(); }
    }

    private void Publish(BlockHeader header, Hash256 root)
    {
        Volatile.Write(ref _cursor, new PbtFollowerCursor(header.Number, header.Hash!, root));
        _shadowRoots[header.Hash!] = root;
    }

    private bool Stall(string error)
    {
        // The scope may hold a half-applied block or sit on an abandoned branch; the next run rebuilds from flat.
        Reset();
        Volatile.Write(ref _error, error);
        return false;
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private sealed class ReplayState(ILifetimeScope lifetime, IWorldState worldState, IDisposable scope, BlockHeader cursor)
    {
        public ILifetimeScope Lifetime => lifetime;
        public IWorldState WorldState => worldState;
        public IDisposable Scope => scope;
        public BlockHeader Cursor { get; set; } = cursor;
    }
}

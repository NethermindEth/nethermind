// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.State.Flat;

namespace Nethermind.State.Pbt.Migration;

/// <summary>Keeps PBT on the canonical head by scheduling BAL catch-up whenever the head moves.</summary>
public sealed class PbtBalHeadFollower(
    PbtBalFollowerScheduler scheduler,
    PbtBalFollower follower,
    IBlockTree blockTree,
    IPbtDbManager manager,
    IFlatDbManager flatDbManager,
    ISpecProvider specProvider,
    ILogManager logManager) : IAsyncDisposable
{
    private readonly ILogger _logger = logManager.GetClassLogger<PbtBalHeadFollower>();
    private readonly object _gate = new();
    private bool _disposed;
    private bool _started;
    private bool _flatFlushed;
    private PbtFollowerCursor? _headCursor;

    public string? Error => scheduler.Error ?? follower.Error;

    /// <summary>The follower's cursor, or the head itself once PBT holds it.</summary>
    public PbtFollowerCursor? Cursor => Volatile.Read(ref _headCursor) ?? follower.Cursor;

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) return;
            _started = true;
            blockTree.OnUpdateMainChain += OnCanonicalChanged;
            blockTree.OnForkChoiceUpdated += OnForkChoiceUpdated;
            ScheduleCurrentHead();
        }
    }

    private void OnCanonicalChanged(object? sender, OnUpdateMainChainArgs args) => ScheduleCurrentHead();

    private void OnForkChoiceUpdated(object? sender, IBlockTree.ForkChoiceUpdateEventArgs args)
    {
        lock (_gate)
        {
            if (!_disposed) FlushFlatOnce();
        }
    }

    private void ScheduleCurrentHead()
    {
        lock (_gate)
        {
            if (_disposed || blockTree.Head is not { } head) return;
            FlushFlatOnce();
            if (manager.HasStateForBlock(new StateId(head.Header)))
            {
                // PBT already holds the head; the follower is only needed for delayed catch-up.
                Volatile.Write(ref _headCursor, PbtFollowerCursor.At(manager, head.Header));
                scheduler.Cancel();
                return;
            }
            Volatile.Write(ref _headCursor, null);
            scheduler.Schedule(head.Header);
        }
    }

    /// <remarks>
    /// Flat gets no commits after activation, so nothing nudges its persistence: persist its remaining
    /// pre-activation states once the activation is final (see <see cref="MigrationFlatFinalizedStateProvider"/>).
    /// </remarks>
    private void FlushFlatOnce()
    {
        if (_flatFlushed || !MigrationActivation.IsFinal(blockTree, specProvider)) return;
        _flatFlushed = true;
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
            _disposed = true;
            if (_started)
            {
                blockTree.OnUpdateMainChain -= OnCanonicalChanged;
                blockTree.OnForkChoiceUpdated -= OnForkChoiceUpdated;
            }
        }
        await scheduler.DisposeAsync();
    }
}

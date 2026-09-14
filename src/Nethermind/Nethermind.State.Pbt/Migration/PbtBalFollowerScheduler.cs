// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.State.Flat;

namespace Nethermind.State.Pbt.Migration;

/// <summary>Serializes delayed BAL catch-up and cancels obsolete targets before starting their replacements.</summary>
internal sealed class PbtBalFollowerScheduler(Func<BlockHeader, CancellationToken, Task<bool>> follow, Func<string?> followerError, Func<PbtFollowerCursor?> followerCursor) : IAsyncDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cancellation;
    private Task _pending = Task.CompletedTask;
    private bool _disposed;
    private string? _error;
    private PbtFollowerCursor? _headCursor;

    private IBlockTree? _blockTree;
    private IPbtDbManager? _manager;
    private IFlatDbManager? _flatDbManager;
    private ISpecProvider? _specProvider;
    private ILogger? _logger;
    private bool _started;
    private bool _flatFlushed;

    public PbtBalFollowerScheduler(PbtBalFollower follower, IBlockTree blockTree, IPbtDbManager manager,
        IFlatDbManager flatDbManager, ISpecProvider specProvider, ILogManager logManager)
        : this(follower.Follow, () => follower.Error, () => follower.Cursor)
    {
        _blockTree = blockTree;
        _manager = manager;
        _flatDbManager = flatDbManager;
        _specProvider = specProvider;
        _logger = logManager.GetClassLogger<PbtBalFollowerScheduler>();
    }

    public string? Error => Volatile.Read(ref _error) ?? followerError();

    /// <summary>The follower's cursor, or the head itself once PBT holds it.</summary>
    public PbtFollowerCursor? Cursor => Volatile.Read(ref _headCursor) ?? followerCursor();

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started || _blockTree is null) return;
            _started = true;
            _blockTree.OnUpdateMainChain += OnCanonicalChanged;
            _blockTree.OnForkChoiceUpdated += OnForkChoiceUpdated;
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
            if (_disposed || _blockTree?.Head is not { } head) return;
            FlushFlatOnce();
            StateId headState = new(head.Header);
            if (_manager!.HasStateForBlock(headState))
            {
                // Main processing mirrored this block; the follower is only needed for delayed catch-up.
                Volatile.Write(ref _headCursor, Describe(head.Header, headState));
                _cancellation?.Cancel();
                return;
            }
            Volatile.Write(ref _headCursor, null);
            Schedule(head.Header);
        }
    }

    private PbtFollowerCursor Describe(BlockHeader header, in StateId stateId)
    {
        using PbtReadOnlySnapshotBundle? bundle = _manager!.TryGatherReadOnlyBundle(stateId);
        return new PbtFollowerCursor(header.Number, header.Hash!, (bundle?.TreeRoot ?? default).ToHash256());
    }

    /// <remarks>
    /// Flat gets no commits after activation, so nothing nudges its persistence: persist its remaining
    /// pre-activation states once the activation is final (see <see cref="MigrationFlatFinalizedStateProvider"/>).
    /// </remarks>
    private void FlushFlatOnce()
    {
        if (_flatFlushed || _blockTree!.FinalizedHash is not { } finalizedHash || finalizedHash == Hash256.Zero) return;
        BlockHeader? finalized = _blockTree.FindHeader(finalizedHash, BlockTreeLookupOptions.None);
        if (finalized is null || !_specProvider!.GetSpec(finalized).IsEip8347Enabled) return;
        _flatFlushed = true;
        IFlatDbManager flat = _flatDbManager!;
        ILogger logger = _logger!.Value;
        Task.Run(() =>
        {
            try { flat.FlushCache(CancellationToken.None); }
            catch (Exception exception)
            {
                if (logger.IsError) logger.Error("Persisting the pre-activation flat state failed", exception);
            }
        });
    }

    public void Schedule(BlockHeader target)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _cancellation?.Cancel();
            CancellationTokenSource cancellation = new();
            _cancellation = cancellation;
            Task previous = _pending;
            _pending = Task.Run(async () =>
            {
                await previous;
                try
                {
                    while (true)
                    {
                        cancellation.Token.ThrowIfCancellationRequested();
                        try
                        {
                            if (await follow(target, cancellation.Token))
                            {
                                Volatile.Write(ref _error, null);
                                break;
                            }
                            Volatile.Write(ref _error, "BAL catch-up is stalled; required canonical data is unavailable.");
                        }
                        catch (Exception exception) when (exception is not OperationCanceledException)
                        {
                            Volatile.Write(ref _error, exception.Message);
                        }
                        await Task.Delay(TimeSpan.FromSeconds(1), cancellation.Token);
                    }
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                catch (Exception exception) { Volatile.Write(ref _error, exception.Message); }
                finally
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null;
                        cancellation.Dispose();
                    }
                }
            });
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task pending;
        lock (_gate)
        {
            _disposed = true;
            if (_started)
            {
                _blockTree!.OnUpdateMainChain -= OnCanonicalChanged;
                _blockTree.OnForkChoiceUpdated -= OnForkChoiceUpdated;
            }
            _cancellation?.Cancel();
            pending = _pending;
        }
        await pending;
    }
}

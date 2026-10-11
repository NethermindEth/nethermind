// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.ReqResp.Protocols;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using ILogger = Nethermind.Logging.ILogger;

namespace Nethermind.BeaconChain.Sync;

/// <summary>Backfills canonical blocks and sampled columns through the retention range (fulu/p2p-interface.md).</summary>
public sealed class ColumnBackfill(
    BeaconChainStore store,
    DataColumnSidecarPool pool,
    RangeSync rangeSync,
    IBeaconSyncPeerPool peerPool,
    SlotClock clock,
    BeaconChainSpec spec,
    IBeaconChainStatusSource status,
    INodeColumnCustodySource custodySource,
    ILogManager logManager)
{
    internal const ulong WindowSlots = RangeSync.DefaultBatchSize;

    internal const int MaxConcurrentColumnFetches = 1;

    private const int MaxBlockPeers = 3;

    private const ulong FollowingHeadSlackSlots = 2;

    internal const string ProgressKey = "columnBackfillFloor";

    private readonly ILogger _logger = logManager.GetClassLogger<ColumnBackfill>();
    private readonly Dictionary<Hash256, RangeSync.ColumnFetchRotation> _rotations = [];
    private long _completeFrom = long.MaxValue;
    private int _peerCursor;
    private ulong _blockedSlot;
    private int _blockedAttempts;

    internal TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How many attempts in a row one slot may stay incomplete before it is reported as a warning; at the default <see cref="RetryDelay"/> about 5 minutes.</summary>
    internal const int BlockedAttemptsBeforeWarning = 10;

    internal TimeSpan WindowPause { get; init; } = TimeSpan.FromMilliseconds(200);
    internal TimeSpan HeadPollDelay { get; init; } = TimeSpan.FromSeconds(1);

    internal ulong? CompleteFrom
    {
        get => Volatile.Read(ref _completeFrom) is long from && from != long.MaxValue ? (ulong)from : null;
        init => _completeFrom = value is { } from ? (long)from : long.MaxValue;
    }

    /// <summary>Verifies contiguous ranges before advertising them and stops on cancellation (fulu/p2p-interface.md).</summary>
    public async Task RunAsync(CancellationToken token)
    {
        try
        {
            await BackfillAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            if (_logger.IsError) _logger.Error($"Data column backfill stopped: {PeerManager.DescribeFailure(e)}");
        }
    }

    private async Task BackfillAsync(CancellationToken token)
    {
        NodeColumnCustody custody = await WaitForCustodyAsync(token);
        ulong floor = ResumeFloor(custody, token);
        if (_logger.IsInfo) _logger.Info($"Data column backfill starts from slot {floor}, wall-clock target {BoundarySlot()}");
        while (floor > BoundarySlot())
        {
            token.ThrowIfCancellationRequested();
            if (!IsFollowingHead())
            {
                await Task.Delay(HeadPollDelay, token);
                continue;
            }

            long failureVersion = pool.WriteFailureVersion;
            ulong previousFloor = floor;
            (ulong newFloor, WindowOutcome outcome) = await TryProcessWindowAsync(floor, custody, token);
            if (newFloor < floor)
            {
                floor = newFloor;
                Publish(floor, previousFloor - 1, failureVersion);
            }

            if (outcome != WindowOutcome.HeadBehind)
            {
                await Task.Delay(outcome == WindowOutcome.Complete ? WindowPause : RetryDelay, token);
            }
        }

        if (_logger.IsInfo) _logger.Info($"Data column backfill is complete: blocks and sampled columns are held from slot {floor}");
    }

    private async Task<NodeColumnCustody> WaitForCustodyAsync(CancellationToken token)
    {
        while (true)
        {
            if (custodySource.Current is { } custody)
            {
                return custody;
            }

            await Task.Delay(HeadPollDelay, token);
        }
    }

    private ulong ResumeFloor(NodeColumnCustody custody, CancellationToken token)
    {
        long failureVersion = pool.WriteFailureVersion;
        ulong floor = Math.Min(pool.EarliestCompletelyServableSlot, clock.CurrentSlot + 1);
        ulong originalFloor = floor;
        if (ReadProgress() is { } stored && stored < floor)
        {
            stored = Math.Max(stored, BoundarySlot());
            ulong verified = floor;
            if (TryFindWalkStart(floor, token, out Hash256? parent, out ForkedSignedBeaconBlock? child))
            {
                while (verified > stored)
                {
                    token.ThrowIfCancellationRequested();
                    if (!store.TryGetForkedBlock(parent!, out ForkedSignedBeaconBlock? block)
                        || block.ComputeMessageRoot() != parent || child is not null && block.Slot >= child.Slot)
                    {
                        break;
                    }

                    if (block.Slot < stored)
                    {
                        verified = stored;
                        break;
                    }

                    if (!store.TryGetCanonicalRoot(block.Slot, out Hash256? indexed) || indexed != parent
                        || NeedsColumns(block, child) && !HoldsColumns(parent!, custody))
                    {
                        verified = Math.Min(verified, block.Slot + 1);
                        break;
                    }

                    verified = Math.Min(verified, block.Slot);
                    child = block;
                    parent = block.ParentRoot;
                }
            }

            if (verified < floor)
            {
                floor = verified;
                Publish(floor, originalFloor - 1, failureVersion, persist: false);
                return floor;
            }
        }

        Volatile.Write(ref _completeFrom, (long)floor);
        return floor;
    }

    private ulong? ReadProgress()
    {
        try
        {
            return store.GetMetadata(ProgressKey) is { Length: sizeof(ulong) } value ? BinaryPrimitives.ReadUInt64BigEndian(value) : null;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            if (_logger.IsWarn) _logger.Warn($"Data column backfill could not read its stored progress and starts over: {PeerManager.DescribeFailure(e)}");
            return null;
        }
    }

    private void Publish(ulong floor, ulong verifiedThrough, long failureVersion, bool persist = true)
    {
        if (persist)
        {
            try
            {
                byte[] value = new byte[sizeof(ulong)];
                BinaryPrimitives.WriteUInt64BigEndian(value, floor);
                store.PutMetadata(ProgressKey, value);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                if (_logger.IsWarn) _logger.Warn($"Data column backfill could not store its progress at slot {floor}: {PeerManager.DescribeFailure(e)}");
            }
        }

        store.BackfilledBlockFloor = floor;
        pool.LowerCompletelyServableFloor(floor, verifiedThrough, failureVersion);
        Volatile.Write(ref _completeFrom, (long)floor);
        if (_logger.IsDebug) _logger.Debug($"Blocks and sampled columns are held from slot {floor}");
    }

    private ulong BoundarySlot() => DataAvailabilityBoundary.ComputeStartSlot(clock.CurrentEpoch, spec);
    private bool IsFollowingHead() => status.CurrentStatus.HeadSlot + FollowingHeadSlackSlots >= clock.CurrentSlot;

    private enum WindowOutcome
    {
        Complete,
        Incomplete,
        HeadBehind,
    }

    private async Task<(ulong Floor, WindowOutcome Outcome)> TryProcessWindowAsync(ulong floor, NodeColumnCustody custody, CancellationToken token)
    {
        try
        {
            return await ProcessWindowAsync(floor, custody, token);
        }
        catch (Exception e) when (e is not OperationCanceledException && e is not OutOfMemoryException)
        {
            if (_logger.IsWarn) _logger.Warn($"Data column backfill failed below slot {floor} and retries in {RetryDelay.TotalSeconds:F0} s: {PeerManager.DescribeFailure(e)}");
            return (floor, WindowOutcome.Incomplete);
        }
    }

    private async Task<(ulong Floor, WindowOutcome Outcome)> ProcessWindowAsync(ulong floor, NodeColumnCustody custody, CancellationToken token)
    {
        ulong boundary = BoundarySlot();
        if (floor <= boundary)
        {
            return (floor, WindowOutcome.Complete);
        }

        ulong lowest = floor - boundary > WindowSlots ? floor - WindowSlots : boundary;
        if (!TryFindWalkStart(floor, token, out Hash256? parent, out ForkedSignedBeaconBlock? child))
        {
            if (_logger.IsDebug) _logger.Debug($"No stored canonical block reaches slot {floor}; the backfill waits");
            return (floor, WindowOutcome.Incomplete);
        }

        List<(Hash256 Root, ForkedSignedBeaconBlock Block, ForkedSignedBeaconBlock? Child)> blocks = [];
        if (!await CollectBlocksAsync(parent!, child, floor, lowest, blocks, token))
        {
            return (floor, IsFollowingHead() ? WindowOutcome.Incomplete : WindowOutcome.HeadBehind);
        }

        List<(Hash256 Root, ForkedSignedBeaconBlock Block, RangeSync.ColumnFetchRotation Rotation)> needing = [];
        foreach ((Hash256 root, ForkedSignedBeaconBlock block, ForkedSignedBeaconBlock? blockChild) in blocks)
        {
            if (NeedsColumns(block, blockChild) && !HoldsColumns(root, custody))
            {
                if (!_rotations.TryGetValue(root, out RangeSync.ColumnFetchRotation? rotation))
                {
                    _rotations[root] = rotation = new RangeSync.ColumnFetchRotation(clock);
                }

                needing.Add((root, block, rotation));
            }
        }

        HashSet<Hash256> pending = [];
        foreach ((Hash256 root, _, _) in needing)
        {
            pending.Add(root);
        }

        List<Hash256> completed = [];
        foreach (Hash256 root in _rotations.Keys)
        {
            if (!pending.Contains(root)) completed.Add(root);
        }

        foreach (Hash256 root in completed) _rotations.Remove(root);

        using SemaphoreSlim slots = new(MaxConcurrentColumnFetches);
        List<Task> fetches = [];
        foreach ((Hash256 root, ForkedSignedBeaconBlock block, RangeSync.ColumnFetchRotation rotation) in needing)
        {
            fetches.Add(FetchColumnsAsync(root, block, rotation, custody, slots, token));
        }

        await Task.WhenAll(fetches);
        // Fetched columns reach the store through the pool's writer; the progress saved from this check must be durable.
        await pool.WhenStored().WaitAsync(token);

        (Hash256 Root, ulong Slot, RangeSync.ColumnFetchRotation Rotation)? highestIncomplete = null;
        foreach ((Hash256 root, ForkedSignedBeaconBlock block, RangeSync.ColumnFetchRotation rotation) in needing)
        {
            if (!HoldsColumns(root, custody) && (highestIncomplete is not { } known || block.Slot > known.Slot))
            {
                highestIncomplete = (root, block.Slot, rotation);
            }
        }

        if (highestIncomplete is not ({ } missingRoot, ulong missingSlot, { } missingRotation))
        {
            return (lowest, WindowOutcome.Complete);
        }

        LogBlocked(missingRoot, missingSlot, missingRotation, custody);
        return (Math.Min(floor, missingSlot + 1), IsFollowingHead() ? WindowOutcome.Incomplete : WindowOutcome.HeadBehind);
    }

    private void LogBlocked(Hash256 root, ulong slot, RangeSync.ColumnFetchRotation rotation, NodeColumnCustody custody)
    {
        _blockedAttempts = slot == _blockedSlot ? _blockedAttempts + 1 : 1;
        _blockedSlot = slot;
        bool stuck = _blockedAttempts % BlockedAttemptsBeforeWarning == 0;
        if (stuck ? !_logger.IsWarn : !_logger.IsDebug)
        {
            return;
        }

        List<ulong> missing = [];
        foreach (ulong column in custody.SampledColumns)
        {
            if (!store.HasDataColumnRecord(root, column)) missing.Add(column);
        }

        string text = $"Data column backfill cannot complete slot {slot} yet (attempt {_blockedAttempts}): columns {string.Join(", ", missing)} are missing after asking {rotation.AskedCount} custodian{(rotation.AskedCount == 1 ? "" : "s")}; the columns are held from slot {slot + 1} and are asked for again in {RetryDelay.TotalSeconds:F0} s";
        if (stuck) _logger.Warn(text);
        else _logger.Debug(text);
    }

    private async Task<bool> CollectBlocksAsync(
        Hash256 parent,
        ForkedSignedBeaconBlock? child,
        ulong floor,
        ulong lowest,
        List<(Hash256 Root, ForkedSignedBeaconBlock Block, ForkedSignedBeaconBlock? Child)> collected,
        CancellationToken token)
    {
        ulong resolvedFrom = floor;
        bool rangeFetched = false;
        while (resolvedFrom > lowest)
        {
            token.ThrowIfCancellationRequested();
            if (!store.TryGetForkedBlock(parent, out ForkedSignedBeaconBlock? block))
            {
                if (!IsFollowingHead())
                {
                    return false;
                }

                if (!rangeFetched)
                {
                    rangeFetched = true;
                    await FetchRangeAsync(parent, lowest, resolvedFrom, token);
                    continue;
                }

                if (!await FetchByRootAsync(parent, token) || !store.TryGetForkedBlock(parent, out block))
                {
                    return false;
                }
            }

            if (block.ComputeMessageRoot() != parent || child is not null && block.Slot >= child.Slot)
            {
                return false;
            }

            if (block.Slot < lowest)
            {
                break;
            }

            if (block.Slot < resolvedFrom)
            {
                collected.Add((parent, block, child));
                if (store.TryGetAnchor(out _, out ulong anchorSlot) && block.Slot >= anchorSlot)
                {
                    if (!store.TryGetCanonicalRoot(block.Slot, out Hash256? canonical) || canonical != parent) return false;
                }
                else
                {
                    store.SetCanonicalRoot(block.Slot, parent);
                }
                resolvedFrom = block.Slot;
            }

            child = block;
            parent = block.ParentRoot;
        }

        return true;
    }

    private bool TryFindWalkStart(ulong floor, CancellationToken token, out Hash256? parent, out ForkedSignedBeaconBlock? child)
    {
        ulong top = store.GetCanonicalIndexTopSlot() ?? 0;
        for (ulong slot = floor; slot <= top; slot++)
        {
            token.ThrowIfCancellationRequested();
            if (store.TryGetCanonicalRoot(slot, out Hash256? root) && store.TryGetForkedBlock(root, out ForkedSignedBeaconBlock? above))
            {
                parent = above.ParentRoot;
                child = above;
                return true;
            }
        }

        child = null;
        ulong lowestIndexed = store.TryGetAnchor(out _, out ulong anchorSlot) ? anchorSlot : 0;
        for (ulong slot = Math.Min(floor - 1, top); slot >= lowestIndexed; slot--)
        {
            token.ThrowIfCancellationRequested();
            if (store.TryGetCanonicalRoot(slot, out parent) && store.TryGetForkedBlock(parent, out _))
            {
                return true;
            }

            if (slot == 0)
            {
                break;
            }
        }

        parent = null;
        return false;
    }

    /// <remarks>
    /// The walk links blocks from <paramref name="parent"/> at the top of the range down, so a reply cut short, which holds the lowest slots, links only once
    /// the slots above it arrive: the next peer is asked for those alone, and the cut reply's blocks are linked with its answer, never stored on their own.
    /// When a whole reply links yet the walk stops above kept blocks, they are dropped and their slots asked for again.
    /// A peer is penalized only once a linked block shows its reply skipped a slot it covered, which BeaconBlocksByRange forbids.
    /// </remarks>
    private async Task FetchRangeAsync(Hash256 parent, ulong from, ulong until, CancellationToken token)
    {
        Dictionary<Hash256, ForkedSignedBeaconBlock> kept = [];
        List<(IBeaconSyncPeer Peer, ulong From, ulong Last, HashSet<Hash256> Roots)> claims = [];
        HashSet<IBeaconSyncPeer> penalized = [];
        ulong askFrom = from;
        foreach (IBeaconSyncPeer peer in NextPeers(from))
        {
            if (!IsFollowingHead()) return;
            IReadOnlyList<ForkedSignedBeaconBlock> reply;
            try
            {
                reply = await peer.RequestBlocksByRangeAsync(askFrom, until - askFrom, token);
            }
            catch (Exception e) when (e is not OperationCanceledException || !token.IsCancellationRequested)
            {
                peer.ReportFailure(PeerFailureClassifier.Classify(e), $"Blocks-by-range [{askFrom}, {until}) failed: {PeerManager.DescribeFailure(e)}");
                // BeaconBlocksByRange: a reply is in slot order, so what a failed one delivered is the lowest part of the range.
                if (e is PartialBlocksException { Received: [.., { } last] received } && last.Slot >= askFrom && last.Slot < until - 1)
                {
                    HashSet<Hash256> roots = [];
                    foreach (ForkedSignedBeaconBlock block in received)
                    {
                        if (block.Slot >= askFrom)
                        {
                            Hash256 root = block.ComputeMessageRoot();
                            kept[root] = block;
                            roots.Add(root);
                        }
                    }

                    claims.Add((peer, askFrom, last.Slot, roots));
                    askFrom = last.Slot + 1;
                }

                continue;
            }

            if ((ulong)reply.Count > until - askFrom)
            {
                peer.ReportFailure(PeerFailureReason.ProtocolViolation, "Too many blocks in range reply");
                continue;
            }

            Dictionary<Hash256, ForkedSignedBeaconBlock> byRoot = new(kept);
            HashSet<Hash256> replyRoots = [];
            ulong replyLast = askFrom;
            foreach (ForkedSignedBeaconBlock block in reply)
            {
                if (block.Slot >= askFrom && block.Slot < until)
                {
                    Hash256 root = block.ComputeMessageRoot();
                    byRoot[root] = block;
                    replyRoots.Add(root);
                    replyLast = Math.Max(replyLast, block.Slot);
                }
            }

            if (replyRoots.Count > 0)
            {
                claims.Add((peer, askFrom, replyLast, replyRoots));
            }

            int stored = 0;
            ulong lowestLinked = until;
            while (byRoot.Remove(parent, out ForkedSignedBeaconBlock? next))
            {
                if (!TryStore(parent, next))
                {
                    return;
                }

                foreach ((IBeaconSyncPeer claimant, ulong claimFrom, ulong claimLast, HashSet<Hash256> claimRoots) in claims)
                {
                    if (next.Slot >= claimFrom && next.Slot <= claimLast && !claimRoots.Contains(parent) && penalized.Add(claimant))
                    {
                        claimant.ReportFailure(PeerFailureReason.ProtocolViolation, $"Blocks-by-range from slot {claimFrom} left out the block at slot {next.Slot}");
                    }
                }

                if (!kept.Remove(parent))
                {
                    stored++;
                }

                lowestLinked = next.Slot;
                parent = next.ParentRoot;
            }

            if (stored != reply.Count)
            {
                peer.ReportFailure(PeerFailureReason.ProtocolViolation, $"Blocks-by-range [{askFrom}, {until}) returned {reply.Count - stored} blocks that do not link to the chain being fetched");
            }

            if (kept.Count == 0)
            {
                return;
            }

            // A reply with no unlinked block, even an empty one, leaves the parent the walk stopped at below it, among or under the kept blocks, so their slots are asked for again.
            if (stored == reply.Count)
            {
                kept.Clear();
                askFrom = from;
            }

            if (lowestLinked <= askFrom)
            {
                return;
            }

            until = lowestLinked;
        }
    }

    private async Task<bool> FetchByRootAsync(Hash256 root, CancellationToken token)
    {
        foreach (IBeaconSyncPeer peer in NextPeers(ulong.MaxValue))
        {
            if (!IsFollowingHead()) return false;
            IReadOnlyList<ForkedSignedBeaconBlock> reply;
            try
            {
                reply = await peer.RequestBlocksByRootAsync([root], token);
            }
            catch (Exception e) when (e is not OperationCanceledException || !token.IsCancellationRequested)
            {
                peer.ReportFailure(PeerFailureClassifier.Classify(e), $"Blocks-by-root for {root} failed: {PeerManager.DescribeFailure(e)}");
                continue;
            }

            if (reply.Count == 1 && reply[0].ComputeMessageRoot() == root)
            {
                return TryStore(root, reply[0]);
            }

            if (reply.Count > 0)
            {
                peer.ReportFailure(PeerFailureReason.ProtocolViolation, $"Blocks-by-root for {root} returned another block");
            }
        }

        return false;
    }

    private bool TryStore(Hash256 root, ForkedSignedBeaconBlock block)
    {
        try
        {
            store.PutForkedBlock(root, block);
            return true;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            if (_logger.IsError) _logger.Error($"Data column backfill could not store the block at slot {block.Slot}: {PeerManager.DescribeFailure(e)}");
            return false;
        }
    }

    private IEnumerable<IBeaconSyncPeer> NextPeers(ulong fromSlot)
    {
        List<IBeaconSyncPeer> peers = [];
        foreach (IBeaconSyncPeer peer in peerPool.GetBestPeers(0))
        {
            if (peer.EarliestAvailableSlot <= fromSlot) peers.Add(peer);
        }
        for (int i = 0; i < Math.Min(MaxBlockPeers, peers.Count); i++)
        {
            int index = _peerCursor % peers.Count;
            _peerCursor = _peerCursor == int.MaxValue ? 0 : _peerCursor + 1;
            yield return peers[index];
        }
    }

    // A child's bid identifies whether the parent's payload was revealed (gloas/fork-choice.md).
    private static bool NeedsColumns(ForkedSignedBeaconBlock block, ForkedSignedBeaconBlock? child) => block switch
    {
        ForkedSignedBeaconBlock.OfFulu { Block.Message.Body.BlobKzgCommitments.Length: > 0 } => true,
        ForkedSignedBeaconBlock.OfGloas { Block.Message.Body.SignedExecutionPayloadBid.Message: { BlobKzgCommitments.Length: > 0 } bid } =>
            child is not ForkedSignedBeaconBlock.OfGloas { Block.Message.Body.SignedExecutionPayloadBid.Message: { } childBid } || childBid.ParentBlockHash == bid.BlockHash,
        _ => false,
    };

    private bool HoldsColumns(Hash256 root, NodeColumnCustody custody)
    {
        foreach (ulong column in custody.SampledColumns)
        {
            if (!store.HasDataColumnRecord(root, column))
            {
                return false;
            }
        }

        return true;
    }

    private async Task FetchColumnsAsync(Hash256 root, ForkedSignedBeaconBlock block, RangeSync.ColumnFetchRotation rotation, NodeColumnCustody custody, SemaphoreSlim slots, CancellationToken token)
    {
        await slots.WaitAsync(token);
        try
        {
            if (!IsFollowingHead())
            {
                return;
            }

            foreach (ulong column in custody.SampledColumns)
            {
                if (store.HasDataColumnRecord(root, column)) continue;
                if (block is ForkedSignedBeaconBlock.OfFulu && pool.TryGet(root, column, out DataColumnSidecar? cached))
                {
                    store.PutDataColumnSidecar(root, block.Slot, cached!);
                }
                else if (block is ForkedSignedBeaconBlock.OfGloas && pool.TryGetGloas(root, column, out DataColumnSidecarGloas? cachedGloas))
                {
                    store.PutDataColumnSidecar(cachedGloas!);
                }
            }

            switch (block)
            {
                case ForkedSignedBeaconBlock.OfFulu fulu:
                    await rangeSync.FetchColumnsByRootAsync(root, fulu.Block.Message!, rotation, token);
                    break;
                case ForkedSignedBeaconBlock.OfGloas gloas:
                    await rangeSync.FetchGloasColumnsByRootAsync(root, gloas.Block.Message!.Body!.SignedExecutionPayloadBid!.Message!, rotation, token);
                    break;
            }
        }
        finally
        {
            slots.Release();
        }
    }
}

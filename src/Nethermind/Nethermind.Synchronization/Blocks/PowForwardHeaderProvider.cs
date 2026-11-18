// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Peers.AllocationStrategies;
using Nethermind.Synchronization.Reporting;

namespace Nethermind.Synchronization.Blocks;

public class PowForwardHeaderProvider(
    ISealValidator sealValidator,
    IBlockTree blockTree,
    ISyncPeerPool syncPeerPool,
    ISyncReport syncReport,
    ILogManager logManager
) : IForwardHeaderProvider
{
    public const int MaxReorganizationLength = 128 * 2;
    private ILogger _logger = logManager.GetClassLogger<PowForwardHeaderProvider>();
    private readonly int[] _ancestorJumps = { 1, 2, 3, 8, 16, 32, 64, 128, 256, 384, 512, 640, 768, 896, 1024 };
    private int _ancestorLookupLevel;
    private ulong _currentNumber;
    private readonly Random _rnd = new();
    private readonly Guid _sealValidatorUserGuid = Guid.NewGuid();
    private readonly Guid _sealValidationBatchGuid = Guid.NewGuid();

    protected const int MinCachedHeaderBatchSize = 32;

    private PeerInfo? _currentBestPeer;
    private IOwnedReadOnlyList<BlockHeader>? _lastResponseBatch = null;

    private IOwnedReadOnlyList<BlockHeader>? LastResponseBatch
    {
        get => _lastResponseBatch;
        set
        {
            _lastResponseBatch?.Dispose();
            _lastResponseBatch = value;
        }
    }

    public virtual async Task<IOwnedReadOnlyList<BlockHeader?>?> GetBlockHeaders(ulong skipLastN, ulong maxHeaders, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();

        if (TryGetPinnedCachedHeaders(skipLastN, maxHeaders, out IOwnedReadOnlyList<BlockHeader>? pinnedHeaders))
        {
            syncReport.FullSyncBlocksDownloaded.TargetValue = _currentBestPeer!.HeadNumber;
            return pinnedHeaders;
        }

        // PrepareRequest must return to the dispatcher when no better peer exists so a feed-state
        // transition is observed; an unbounded allocation would remain parked inside this method.
        using SyncPeerAllocation allocation = await syncPeerPool.Allocate(
            new ForwardAllocationStrategy(new ByTotalDifficultyPeerAllocationStrategy(null), peer =>
            {
                UInt256? localDifficulty = blockTree.BestSuggestedHeader?.TotalDifficulty;
                bool difficultyEligible = localDifficulty is null || peer.TotalDifficulty > localDifficulty;
                return (difficultyEligible || HeightProbeEligible(peer, skipLastN))
                    && ImprovementRequirementSatisfied(peer);
            }, peer => peer.HeadNumber.SaturatingSub(skipLastN) > blockTree.BestKnownNumber),
            AllocationContexts.ForwardHeader,
            timeoutMilliseconds: 0,
            cancellationToken: cancellation);

        PeerInfo? peerInfo = allocation.Current;
        if (peerInfo is null) return null;

        if (peerInfo != _currentBestPeer)
        {
            OnNewBestPeer(peerInfo);
        }

        syncReport.FullSyncBlocksDownloaded.TargetValue = peerInfo.HeadNumber;

        if (_logger.IsTrace) _logger.Trace($"Allocated {peerInfo:hide} for PoW header info. currentNumber: {_currentNumber} skipLastN: {skipLastN}, maxHeaders: {maxHeaders}");

        IOwnedReadOnlyList<BlockHeader?>? headers = AssembleResponseFromLastResponseBatch(peerInfo.HeadNumber, skipLastN, maxHeaders);
        if (headers is not null)
        {
            ReadOnlySpan<BlockHeader?> headersSpan = headers.AsSpan();
            if (_logger.IsTrace) _logger.Trace($"PoW header info from last response from {headersSpan[0].ToString(BlockHeader.Format.Short)} to {headersSpan[^1].ToString(BlockHeader.Format.Short)}");
            return headers;
        }

        headers = await GetBlockHeaders(peerInfo, skipLastN, maxHeaders, cancellation);
        if (headers is not null)
        {
            ReadOnlySpan<BlockHeader?> headersSpan = headers.AsSpan();
            if (_logger.IsTrace) _logger.Trace($"Assembled batch from {peerInfo:hide} of {headersSpan.Length} header from {headersSpan[0].ToString(BlockHeader.Format.Short)} to {headersSpan[^1].ToString(BlockHeader.Format.Short)}");
        }
        else
        {
            if (_logger.IsTrace) _logger.Trace($"No header received");
            _currentBestPeer = null;
        }

        if (headers is not null && headers.Count > 1) LastResponseBatch = headers.AsSpan().ToPooledList();
        return headers;
    }

    private bool TryGetPinnedCachedHeaders(ulong skipLastN, ulong maxHeaders, out IOwnedReadOnlyList<BlockHeader>? headers)
    {
        headers = null;
        IOwnedReadOnlyList<BlockHeader>? cached = LastResponseBatch;
        PeerInfo? owner = _currentBestPeer;
        if (cached is null || cached.Count < 2 || owner is null)
        {
            return false;
        }

        if (!ReferenceEquals(syncPeerPool.GetPeer(owner.SyncPeer.Node), owner)
            || !owner.IsInitialized
            || owner.IsAsleep(AllocationContexts.ForwardHeader)
            || !ImprovementRequirementSatisfied(owner))
        {
            LastResponseBatch = null;
            return false;
        }

        ulong headNumber = owner.HeadNumber;
        ReadOnlySpan<BlockHeader> cachedSpan = cached.AsSpan();
        if (HasConflictingAdvertisedHead(cachedSpan, headNumber, owner.HeadHash))
        {
            LastResponseBatch = null;
            return false;
        }

        BlockHeader? bestSuggested = blockTree.BestSuggestedHeader;
        int startIndex = IndexOfCurrentNumber(cachedSpan);
        BlockHeader? parent = startIndex < 0 ? null : cachedSpan[startIndex];
        if (parent is null || !blockTree.IsKnownBlock(parent.Number, parent.Hash!))
        {
            LastResponseBatch = null;
            return false;
        }

        if (bestSuggested is null || bestSuggested.Number != _currentNumber)
        {
            return false;
        }

        if (bestSuggested.Hash != parent.Hash)
        {
            LastResponseBatch = null;
            return false;
        }

        headers = AssembleResponseFromLastResponseBatch(cachedSpan, startIndex, headNumber, skipLastN, maxHeaders);
        return headers is not null;
    }

    private static bool HasConflictingAdvertisedHead(ReadOnlySpan<BlockHeader> cached, ulong headNumber, Hash256? headHash)
    {
        for (int i = 0; i < cached.Length; i++)
        {
            BlockHeader header = cached[i];
            if (header.Number == headNumber)
            {
                return header.Hash != headHash;
            }
        }

        return false;
    }

    private int IndexOfCurrentNumber(ReadOnlySpan<BlockHeader> headers)
    {
        for (int i = 0; i < headers.Length; i++)
        {
            if (headers[i].Number == _currentNumber)
            {
                return i;
            }
        }

        return -1;
    }

    private IOwnedReadOnlyList<BlockHeader>? AssembleResponseFromLastResponseBatch(ulong headNumber, ulong skipLastN, ulong maxHeaders)
    {
        IOwnedReadOnlyList<BlockHeader>? lastResponseBatch = LastResponseBatch;
        if (lastResponseBatch is null) return null;

        ReadOnlySpan<BlockHeader> lastResponseBatchSpan = lastResponseBatch.AsSpan();
        int startIndex = IndexOfCurrentNumber(lastResponseBatchSpan);
        if (startIndex < 0)
        {
            LastResponseBatch = null;
            return null;
        }

        return AssembleResponseFromLastResponseBatch(lastResponseBatchSpan, startIndex, headNumber, skipLastN, maxHeaders);
    }

    private IOwnedReadOnlyList<BlockHeader>? AssembleResponseFromLastResponseBatch(ReadOnlySpan<BlockHeader> lastResponseBatchSpan, int startIndex, ulong headNumber, ulong skipLastN, ulong maxHeaders)
    {
        ulong upperDownloadBoundary = headNumber.SaturatingSub(skipLastN);
        if (_currentNumber > upperDownloadBoundary)
        {
            return null;
        }

        ulong available = upperDownloadBoundary - _currentNumber;
        ulong maxCount = available == ulong.MaxValue ? ulong.MaxValue : available + 1;
        ulong remainingCount = (ulong)(lastResponseBatchSpan.Length - startIndex);
        int responseCount = (int)Math.Min(remainingCount, Math.Min(maxHeaders, maxCount));
        if (responseCount < 2)
        {
            if (remainingCount <= 1)
            {
                LastResponseBatch = null;
            }
            return null;
        }

        IOwnedReadOnlyList<BlockHeader> response = lastResponseBatchSpan.Slice(startIndex, responseCount).ToPooledList();
        if (startIndex > 0)
        {
            LastResponseBatch = lastResponseBatchSpan[startIndex..].ToPooledList();
        }

        return response;
    }

    private void OnNewBestPeer(PeerInfo newBestPeer)
    {
        if (_logger.IsTrace) _logger.Trace($"On new best peer. Current best peer: {_currentBestPeer:hide}, new best peer: {newBestPeer:hide}");
        if (newBestPeer?.HeadHash != _currentBestPeer?.HeadHash)
        {
            LastResponseBatch = null;
        }

        // TODO: Is there a (fast) way to know if the new peer's head has the parent of last peer?
        _ancestorLookupLevel = 0;
        ulong bestKnown = blockTree.BestKnownNumber;
        ulong headNum = newBestPeer.HeadNumber;
        _currentNumber = Math.Min(bestKnown, headNum.SaturatingSub(1));
        _currentBestPeer = newBestPeer;
    }

    private async Task<IOwnedReadOnlyList<BlockHeader?>?> GetBlockHeaders(PeerInfo bestPeer, ulong skipLastN, ulong maxHeaders, CancellationToken cancellation)
    {
        while (true)
        {
            if (!ImprovementRequirementSatisfied(bestPeer)) return null;
            if (bestPeer.TotalDifficulty <= blockTree.BestSuggestedHeader?.TotalDifficulty && !HeightProbeEligible(bestPeer, skipLastN)) return null;
            if (_currentNumber > bestPeer!.HeadNumber) return null;

            if (_logger.IsDebug) _logger.Debug($"Continue full sync with {bestPeer:hide} (our best {blockTree.BestKnownNumber})");

            ulong upperDownloadBoundary = bestPeer.HeadNumber.SaturatingSub(skipLastN);
            if (_currentNumber > upperDownloadBoundary)
            {
                return null;
            }
            ulong blocksLeft = upperDownloadBoundary - _currentNumber;
            ulong headersToRequest = blocksLeft >= maxHeaders ? maxHeaders : blocksLeft + 1;
            if (headersToRequest <= 1)
            {
                return null;
            }

            headersToRequest = Math.Min(headersToRequest, (ulong)bestPeer.MaxHeadersPerRequest());
            if (_logger.IsTrace) _logger.Trace($"Full sync request {_currentNumber}+{headersToRequest} to peer {bestPeer:hide} with {bestPeer.HeadNumber} blocks. Got {_currentNumber} and asking for {headersToRequest} more.");

            cancellation.ThrowIfCancellationRequested();
            ulong claimedHead = bestPeer.SyncPeer.HeadNumber;
            Hash256 claimedHash = bestPeer.SyncPeer.HeadHash;
            ulong requestOrdinal = 0;
            try
            {
                IOwnedReadOnlyList<BlockHeader>? headers =
                    await RequestHeaders(bestPeer, cancellation, _currentNumber, headersToRequest, out requestOrdinal);

                {
                    ReadOnlySpan<BlockHeader> headersSpan = headers.AsSpan();
                    if (headersSpan.Length < 2)
                    {
                        bestPeer.DemoteHeadClaim(claimedHash, claimedHead);
                        syncPeerPool.ReportNoSyncProgress(bestPeer, AllocationContexts.ForwardHeader);
                        headers.Dispose();
                        return null;
                    }

                    // Remember, we start downloading from currentNumber+1
                    if (!CheckAncestorJump(bestPeer, headersSpan[0], ref _currentNumber))
                    {
                        headers.Dispose();
                        continue;
                    }
                    ulong servedNumber = _currentNumber;
                    if (sealValidator.RequiresParentForSyncCredit)
                    {
                        // Only the first child has a parent whose consensus snapshot is locally available.
                        if (headersSpan[1] is { } child && sealValidator.CanCreditChild(headersSpan[0], child))
                            servedNumber = child.Number;
                    }
                    else
                    {
                        foreach (BlockHeader? header in headersSpan)
                        {
                            if (header is null) break;
                            servedNumber = header.Number;
                        }
                    }
                    if (servedNumber > _currentNumber)
                    {
                        bestPeer.RecordServedHead(servedNumber);
                        bestPeer.RecordProgress(AllocationContexts.ForwardHeader);
                    }
                }

                return headers;
            }
            catch (TimeoutException) when (!cancellation.IsCancellationRequested)
            {
                if (requestOrdinal != 0) bestPeer.RecordAllocatedTimeout(AllocationContexts.ForwardHeader, requestOrdinal);
                return null;
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                if (requestOrdinal != 0) bestPeer.RecordAllocatedTimeout(AllocationContexts.ForwardHeader, requestOrdinal);
                return null;
            }
            catch (EthSyncException e)
            {
                if (_logger.IsDebug) _logger.Debug($"Failed to download forward header from {bestPeer:hide}, {e:hide}");
                syncPeerPool.ReportBreachOfProtocol(bestPeer, DisconnectReason.ForwardSyncFailed, e.Message);
            }

            return null;
        }
    }

    public virtual void OnSuggestBlock(BlockTreeSuggestOptions options, Block currentBlock, AddBlockResult addResult) => _currentNumber += 1;

    private bool CheckAncestorJump(PeerInfo bestPeer, BlockHeader blockBeforeZero, ref ulong currentNumber)
    {
        bool parentIsKnown = blockTree.IsKnownBlock(blockBeforeZero.Number, blockBeforeZero.Hash!);
        if (!parentIsKnown)
        {
            _ancestorLookupLevel++;
            if (_ancestorLookupLevel >= _ancestorJumps.Length)
            {
                if (_logger.IsWarn) _logger.Warn($"Could not find common ancestor with {bestPeer:hide}");
                throw new EthSyncException("Peer with inconsistent chain in sync");
            }

            int ancestorJump = _ancestorJumps[_ancestorLookupLevel] - _ancestorJumps[_ancestorLookupLevel - 1];
            currentNumber = currentNumber.SaturatingSub((ulong)ancestorJump);
            ulong bestSuggestedNumber = blockTree.BestSuggestedHeader?.Number ?? 0UL;
            ulong minAllowed = bestSuggestedNumber.SaturatingSub((ulong)MaxReorganizationLength);
            currentNumber = Math.Max(minAllowed, currentNumber);
            return false;
        }
        _ancestorLookupLevel = 0;
        return true;
    }

    private Task<IOwnedReadOnlyList<BlockHeader>> RequestHeaders(PeerInfo peer, CancellationToken cancellation,
        ulong currentNumber, ulong headersToRequest, out ulong requestOrdinal)
    {
        ulong start = currentNumber.SaturatingSub(1028);
        sealValidator.HintValidationRange(_sealValidatorUserGuid, start, currentNumber + 30000);
        cancellation.ThrowIfCancellationRequested();
        requestOrdinal = peer.CaptureRequestOrdinal();
        Task<IOwnedReadOnlyList<BlockHeader>?> request = peer.SyncPeer.GetBlockHeaders(currentNumber, (int)headersToRequest, 0, cancellation);
        return ValidateRequestedHeaders(request, peer, cancellation, currentNumber);
    }

    private async Task<IOwnedReadOnlyList<BlockHeader>> ValidateRequestedHeaders(Task<IOwnedReadOnlyList<BlockHeader>?> request,
        PeerInfo peer, CancellationToken cancellation, ulong currentNumber)
    {
        IOwnedReadOnlyList<BlockHeader> headers = await request ?? IOwnedReadOnlyList<BlockHeader>.Empty;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            headers = FilterPosHeader(headers);
            // Reject peer-chosen heights before seals can trigger synchronous Ethash epoch work.
            ValidateBatchConsistency(peer, headers.AsSpan(), currentNumber);
            ValidateSeals(headers, cancellation);
            cancellation.ThrowIfCancellationRequested();
            return headers;
        }
        catch
        {
            headers.Dispose();
            throw;
        }
    }

    private void ValidateBatchConsistency(PeerInfo bestPeer, ReadOnlySpan<BlockHeader> headers, ulong startNumber)
    {
        if (headers.Length > 0 && headers[0] is not null && headers[0].Number != startNumber)
        {
            if (_logger.IsTrace) _logger.Trace($"Block list from peer {bestPeer:hide} does not start at the requested {startNumber}");
            throw new EthSyncException("Peer sent a block list that does not start at the requested number");
        }

        // in the past (version 1.11) and possibly now too Parity was sending non canonical blocks in responses
        // so we need to confirm that the blocks form a valid subchain
        for (int i = 1; i < headers.Length; i++)
        {
            if (headers[i] is null)
            {
                break;
            }

            BlockHeader? previous = headers[i - 1];
            if (previous is null || headers[i].ParentHash != previous.Hash || headers[i].Number != previous.Number + 1)
            {
                if (_logger.IsTrace) _logger.Trace($"Inconsistent block list from peer {bestPeer:hide}");
                throw new EthSyncException("Peer sent an inconsistent block list");
            }
        }
    }

    protected void ValidateSeals(IReadOnlyList<BlockHeader?> headers, CancellationToken cancellation)
    {
        if (_logger.IsTrace) _logger.Trace("Starting seal validation");
        HintBatchRange(headers);
        ConcurrentQueue<Exception> exceptions = new();
        int randomNumberForValidation = _rnd.Next(Math.Max(0, headers.Count - 2));
        Parallel.For(0, headers.Count, (i, state) =>
        {
            if (cancellation.IsCancellationRequested)
            {
                if (_logger.IsTrace) _logger.Trace("Returning fom seal validation");
                state.Stop();
                return;
            }

            BlockHeader? header = headers[i];
            if (header is null)
            {
                return;
            }

            try
            {
                bool lastBlock = i == headers.Count - 1;
                // PoSSwitcher can't determine if a block is a terminal block if TD is missing due to another
                // problem. In theory, this should not be a problem, but additional seal check does no harm.
                bool terminalBlock = !lastBlock
                                     && headers.Count > 1
                                     && headers[i + 1].Difficulty == 0
                                     && headers[i].Difficulty != 0;
                bool forceValidation = lastBlock || i == randomNumberForValidation || terminalBlock;
                if (!sealValidator.ValidateSeal(header, forceValidation))
                {
                    if (_logger.IsTrace) _logger.Trace("One of the seals is invalid");
                    throw new EthSyncException("Peer sent a block with an invalid seal");
                }
            }
            catch (Exception e)
            {
                exceptions.Enqueue(e);
                state.Stop();
            }
        });

        if (_logger.IsTrace) _logger.Trace("Seal validation complete");

        if (!exceptions.IsEmpty)
        {
            if (_logger.IsDebug) _logger.Debug("Seal validation failure");
            cancellation.ThrowIfCancellationRequested();
            AggregateException failure = new(exceptions);
            if (failure.InnerExceptions.All(static e => e is EthSyncException or ArgumentException or InvalidOperationException or BlockchainException))
            {
                throw new EthSyncException("Peer sent a block whose seal could not be validated", failure);
            }

            throw failure;
        }
        cancellation.ThrowIfCancellationRequested();
    }

    // Ethash refuses to validate an epoch it was not hinted for, so the hint has to sit next to the validation:
    // subclasses call ValidateSeals without going through RequestHeaders. A dedicated guid keeps this exact
    // range from evicting the wider pre-warm hint RequestHeaders issues.
    private void HintBatchRange(IReadOnlyList<BlockHeader?> headers)
    {
        ulong min = ulong.MaxValue;
        ulong max = 0;
        for (int i = 0; i < headers.Count; i++)
        {
            BlockHeader? header = headers[i];
            if (header is null) continue;
            if (header.Number < min) min = header.Number;
            if (header.Number > max) max = header.Number;
        }

        if (min <= max)
        {
            sealValidator.HintValidationRange(_sealValidationBatchGuid, min, max);
        }
    }

    protected virtual bool ImprovementRequirementSatisfied(PeerInfo? bestPeer) => bestPeer is not null
        && ((bestPeer.TotalDifficulty ?? UInt256.Zero) > (blockTree.BestSuggestedHeader?.TotalDifficulty ?? UInt256.Zero)
            || HeightProbeEligible(bestPeer, 0));

    private bool HeightProbeEligible(PeerInfo peer, ulong skipLastN) => peer.TotalDifficulty is not null
        && peer.KnownHeadNumber is { } known && peer.HeadNumber.SaturatingSub(skipLastN) > known
        && !peer.HasNoProgress(AllocationContexts.ForwardHeader);

    private sealed class ForwardAllocationStrategy(
        IPeerAllocationStrategy strategy, Func<PeerInfo, bool> eligible, Func<PeerInfo, bool> ahead) : IPeerAllocationStrategy
    {
        public PeerInfo? Allocate(PeerInfo? currentPeer, IEnumerable<PeerInfo> peers, INodeStatsManager stats, IBlockTree tree)
        {
            IEnumerable<PeerInfo> candidates = peers.Where(eligible);
            PeerInfo? current = currentPeer is not null && eligible(currentPeer) ? currentPeer : null;
            return strategy.Allocate(current is not null && ahead(current) ? current : null,
                candidates.Where(ahead), stats, tree)
                ?? strategy.Allocate(current, candidates, stats, tree);
        }
    }

    protected virtual IOwnedReadOnlyList<BlockHeader> FilterPosHeader(IOwnedReadOnlyList<BlockHeader> headers) => headers;
}

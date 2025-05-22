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
using Nethermind.Core.Extensions;
using Nethermind.Logging;
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
    private long _currentNumber;
    private readonly Random _rnd = new();
    private readonly Guid _sealValidatorUserGuid = Guid.NewGuid();

    private const int MinCachedHeaderBatchSize = 32;

    private IPeerAllocationStrategy _bestPeerAllocationStrategy =
        new TotalDiffStrategy(new ByTotalDifficultyPeerAllocationStrategy(null), TotalDiffStrategy.TotalDiffSelectionType.AtLeastTheSame);

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

    public virtual Task<IOwnedReadOnlyList<BlockHeader?>?> GetBlockHeaders(int skipLastN, int maxHeaders, CancellationToken cancellation)
    {
        return syncPeerPool.AllocateAndRun(async (peerInfo) =>
        {
            if (peerInfo != _currentBestPeer)
            {
                OnNewBestPeer(peerInfo);
            }

            syncReport.FullSyncBlocksDownloaded.TargetValue = peerInfo.HeadNumber;

            if (_logger.IsTrace) _logger.Trace($"Allocated {peerInfo} for PoW header info. currentNumber: {_currentNumber} skipLastN: {skipLastN}, maxHeaders: {maxHeaders}");

            // Provide a way so that it does not redownload if part of the. I guess it does not care about skiplastn and maxheaders.
            // TODO: Unit test this mechanism.
            IOwnedReadOnlyList<BlockHeader?>? headers = AssembleResponseFromLastResponseBatch();
            if (headers is not null)
            {
                if (_logger.IsTrace) _logger.Trace($"PoW header info from last response from {headers[0].ToString(BlockHeader.Format.Short)} to {headers[1].ToString(BlockHeader.Format.Short)}");
                return headers;
            }

            headers = await GetBlockHeaders(peerInfo, skipLastN, maxHeaders, cancellation);
            if (headers is not null)
            {
                if (_logger.IsTrace) _logger.Trace($"Assembled batch from {peerInfo} of {headers.Count} header from {headers[0].ToString(BlockHeader.Format.Short)} to {headers[^1].ToString(BlockHeader.Format.Short)}");
            }
            else
            {
                if (_logger.IsTrace) _logger.Trace($"No header received");
            }

            if (headers is not null && headers?.Count > MinCachedHeaderBatchSize) LastResponseBatch = headers.ToPooledList(headers.Count);
            return headers;
        }, _bestPeerAllocationStrategy, AllocationContexts.ForwardHeader, cancellation);
    }

    private IOwnedReadOnlyList<BlockHeader>? AssembleResponseFromLastResponseBatch()
    {
        if (LastResponseBatch is null) return null;

        long currentNumber = _currentNumber;
        bool sameFound = false;
        ArrayPoolList<BlockHeader>? newResponse = null;
        for (int i = 0; i < LastResponseBatch.Count; i++)
        {
            if (!sameFound && LastResponseBatch[i].Number != currentNumber) continue;
            sameFound = true;

            newResponse ??= new ArrayPoolList<BlockHeader>(LastResponseBatch.Count - i);
            newResponse.Add(LastResponseBatch[i]);
        }

        if (newResponse is null || newResponse.Count <= MinCachedHeaderBatchSize)
        {
            LastResponseBatch = null;
            newResponse?.Dispose();
            return null;
        }

        LastResponseBatch = newResponse;
        return LastResponseBatch;
    }

    private void OnNewBestPeer(PeerInfo newBestPeer)
    {
        if (_logger.IsTrace) _logger.Trace($"On new best peer. Current best peer: {_currentBestPeer}, new best peer: {newBestPeer}");
        if (newBestPeer?.HeadHash != _currentBestPeer?.HeadHash)
        {
            LastResponseBatch = null;
        }

        // TODO: Is there a (fast) way to know if the new peer's head has the parent of last peer?
        _ancestorLookupLevel = 0;
        _currentNumber = Math.Max(0, Math.Min(blockTree.BestKnownNumber, newBestPeer.HeadNumber - 1)); // Remember, _currentNumber is -1 than what we want.
        _currentBestPeer = newBestPeer;
    }

    private async Task<IOwnedReadOnlyList<BlockHeader?>?> GetBlockHeaders(PeerInfo bestPeer, int skipLastN, int maxHeaders, CancellationToken cancellation)
    {
        while (true)
        {
            if (!ImprovementRequirementSatisfied(bestPeer)) return null;
            if (_currentNumber > bestPeer!.HeadNumber) return null;

            if (_logger.IsDebug) _logger.Debug($"Continue full sync with {bestPeer} (our best {blockTree.BestKnownNumber})");

            long upperDownloadBoundary = bestPeer.HeadNumber - skipLastN;
            long blocksLeft = upperDownloadBoundary - _currentNumber;
            int headersToRequest = (int)Math.Min(blocksLeft + 1, maxHeaders);
            if (headersToRequest <= 1)
            {
                return null;
            }

            headersToRequest = Math.Min(headersToRequest, bestPeer.MaxHeadersPerRequest());
            if (_logger.IsTrace) _logger.Trace($"Full sync request {_currentNumber}+{headersToRequest} to peer {bestPeer} with {bestPeer.HeadNumber} blocks. Got {_currentNumber} and asking for {headersToRequest} more.");

            cancellation.ThrowIfCancellationRequested();
            try
            {
                IOwnedReadOnlyList<BlockHeader>? headers =
                    await RequestHeaders(bestPeer, cancellation, _currentNumber, headersToRequest);
                if (headers.Count < 2)
                {
                    // Peer dont have new header
                    headers.Dispose();
                    return null;
                }

                // Remember, we start downloading from currentNumber+1
                if (!CheckAncestorJump(bestPeer, headers[0], ref _currentNumber)) continue;

                return headers;
            }
            catch (TimeoutException)
            {
                syncPeerPool.ReportWeakPeer(bestPeer, AllocationContexts.ForwardHeader);
                return null;
            }
            catch (EthSyncException e)
            {
                if (_logger.IsDebug) _logger.Debug($"Failed to download forward header from {bestPeer}, {e}");
                syncPeerPool.ReportBreachOfProtocol(bestPeer, DisconnectReason.ForwardSyncFailed, e.Message);
            }

            return null;
        }
    }

    public virtual void OnSuggestBlock(BlockTreeSuggestOptions options, Block currentBlock, AddBlockResult addResult)
    {
        _currentNumber += 1;
    }

    private bool CheckAncestorJump(PeerInfo bestPeer, BlockHeader blockBeforeZero, ref long currentNumber)
    {
        bool parentIsKnown = blockTree.IsKnownBlock(blockBeforeZero.Number, blockBeforeZero.Hash!);
        if (!parentIsKnown)
        {
            _ancestorLookupLevel++;
            if (_ancestorLookupLevel >= _ancestorJumps.Length)
            {
                if (_logger.IsWarn) _logger.Warn($"Could not find common ancestor with {bestPeer}");
                throw new EthSyncException("Peer with inconsistent chain in sync");
            }

            int ancestorJump = _ancestorJumps[_ancestorLookupLevel] - _ancestorJumps[_ancestorLookupLevel - 1];
            currentNumber = currentNumber >= ancestorJump ? (currentNumber - ancestorJump) : 0L;
            currentNumber = Math.Max((blockTree.BestSuggestedHeader?.Number ?? 0) - MaxReorganizationLength, currentNumber);
            return false;
        }
        _ancestorLookupLevel = 0;
        return true;
    }

    private async Task<IOwnedReadOnlyList<BlockHeader>> RequestHeaders(PeerInfo peer, CancellationToken cancellation, long currentNumber, int headersToRequest)
    {
        sealValidator.HintValidationRange(_sealValidatorUserGuid, currentNumber - 1028, currentNumber + 30000);

        IOwnedReadOnlyList<BlockHeader> headers = await peer.SyncPeer.GetBlockHeaders(currentNumber, headersToRequest, 0, cancellation);
        cancellation.ThrowIfCancellationRequested();
        headers = FilterPosHeader(headers);

        ValidateSeals(headers, cancellation);
        ValidateBatchConsistencyAndSetParents(peer, headers);
        return headers;
    }

    private void ValidateBatchConsistencyAndSetParents(PeerInfo bestPeer, IReadOnlyList<BlockHeader?> headers)
    {
        // in the past (version 1.11) and possibly now too Parity was sending non canonical blocks in responses
        // so we need to confirm that the blocks form a valid subchain
        for (int i = 1; i < headers.Count; i++)
        {
            if (headers[i] is not null && headers[i]?.ParentHash != headers[i - 1]?.Hash)
            {
                if (_logger.IsTrace) _logger.Trace($"Inconsistent block list from peer {bestPeer}");
                throw new EthSyncException("Peer sent an inconsistent block list");
            }

            if (headers[i] is null)
            {
                break;
            }

            if (i != 1) // because we will never set TotalDifficulty on the first block?
            {
                headers[i].MaybeParent = new WeakReference<BlockHeader>(headers[i - 1]);
            }
        }
    }

    protected void ValidateSeals(IReadOnlyList<BlockHeader?> headers, CancellationToken cancellation)
    {
        if (_logger.IsTrace) _logger.Trace("Starting seal validation");
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
            if (exceptions.First() is EthSyncException ethSyncException)
            {
                throw ethSyncException;
            }
            throw new AggregateException(exceptions);
        }
        cancellation.ThrowIfCancellationRequested();
    }

    protected virtual bool ImprovementRequirementSatisfied(PeerInfo? bestPeer)
    {
        return bestPeer!.TotalDifficulty > (blockTree.BestSuggestedHeader?.TotalDifficulty ?? 0);
    }

    protected virtual IOwnedReadOnlyList<BlockHeader> FilterPosHeader(IOwnedReadOnlyList<BlockHeader> headers)
    {
        return headers;
    }
}

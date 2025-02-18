// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc.Data;
using Nethermind.Logging;

namespace Nethermind.Optimism.CL.L1Bridge;

public class EthereumL1Bridge : IL1Bridge
{
    private readonly ICLConfig _config;
    private readonly IEthApi _ethL1Api;
    private readonly IBeaconApi _beaconApi;
    private readonly Task _headUpdateTask;
    private readonly ILogger _logger;
    private readonly CancellationToken _cancellationToken;

    private ulong _currentHeadNumber = 0;
    private Hash256? _currentHeadHash = null;
    private ulong _currentFinalizedNumber;

    public EthereumL1Bridge(IEthApi ethL1Rpc, IBeaconApi beaconApi, ICLConfig config, CancellationToken cancellationToken, ILogManager logManager)
    {
        _logger = logManager.GetClassLogger();
        _config = config;
        _ethL1Api = ethL1Rpc;
        _beaconApi = beaconApi;
        _cancellationToken = cancellationToken;
        _headUpdateTask = new Task(() =>
        {
            HeadUpdateLoop();
        });
    }


    // TODO: remove receipts
    private readonly Channel<L1Block> NewHeadChannel = Channel.CreateBounded<L1Block>(20);
    public ChannelReader<L1Block> NewHeadReader => NewHeadChannel.Reader;


    private async void HeadUpdateLoop()
    {
        while (!_cancellationToken.IsCancellationRequested)
        {
            // TODO: can we do it with subscription?
            L1Block newHead = await GetHead();
            ulong newHeadNumber = newHead.Number;

            int numberOfMissingBlocks = (int)newHeadNumber - (int)_currentHeadNumber - 1;
            if (numberOfMissingBlocks > 64)
            {
                _logger.Error($"Long head update. Number of missing blocks: {numberOfMissingBlocks}, current head number: {_currentHeadNumber}, new head number: {newHeadNumber}");
                // Try to build up instead of rolling back
                // At this point we already got blocks up until _currentHead.
                // if _currentHead was not reorged => we need blocks from _currentHead up to newHead
                // if _currentHead was reorged => we need to re-run all blocks from _currentFinalized up to newHead
                L1Block newFinalized = await GetFinalized();
                L1Block currentHeadBlock = await GetBlock(_currentHeadNumber);
                if (currentHeadBlock.Hash != _currentHeadHash)
                {
                    // Reorg currentHead
                    await BuildUp(_currentFinalizedNumber, newFinalized.Number);
                    await RollBack(newHead.Hash, newHeadNumber, newFinalized.Number);
                }
                else
                {
                    // CurrentHead is ok
                    await BuildUp(_currentHeadNumber, newFinalized.Number); // Will build up if _currentHead < newFinalized
                    await RollBack(newHead.Hash, newHeadNumber, _currentHeadNumber);
                }

                _currentFinalizedNumber = newFinalized.Number;
            }
            else if (numberOfMissingBlocks > 0)
            {
                await RollBack(newHead.Hash, newHeadNumber, _currentHeadNumber);
            }

            // TODO we can have reorg here
            _currentHeadNumber = newHeadNumber;
            _currentHeadHash = newHead.Hash;
            await WriteBlock(newHead);
            await TryUpdateFinalized();

            // TODO: fix number
            await Task.Delay(50000, _cancellationToken);
        }
    }

    private async Task WriteBlock(L1Block block)
    {
        _logger.Error($"Writing block. Number: {block.Number}, Hash: {block.Hash}");
        await NewHeadChannel.Writer.WriteAsync(block, _cancellationToken);
    }

    private async Task TryUpdateFinalized()
    {
        if (_currentHeadNumber - _currentFinalizedNumber >= 64)
        {
            L1Block newFinalized = await GetFinalized();
            if (newFinalized.Number != _currentFinalizedNumber)
            {
                _logger.Error($"New finalized block. Number: {newFinalized.Number}, Hash: {newFinalized.Hash}");
                _currentFinalizedNumber = newFinalized.Number;
            }
        }
    }

    // Gets all blocks from range [{segmentStartNumber}, {headNumber})
    private async Task RollBack(Hash256 headHash, ulong headNumber, ulong segmentStartNumber)
    {
        Hash256 currentHash = headHash;
        L1Block[] chainSegment = new L1Block[headNumber - segmentStartNumber + 1];
        for (int i = chainSegment.Length - 1; i >= 0; i--)
        {
            _logger.Info($"Rolling back L1 head. {i} to go. Block {currentHash}");
            chainSegment[i] = await GetBlockByHash(currentHash);
            currentHash = chainSegment[i].ParentHash;
            if (_cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }

        if (_currentHeadHash is not null && _currentHeadHash != chainSegment[0].ParentHash)
        {
            // TODO: chain reorg
        }

        for (int i = 0; i < chainSegment.Length; i++)
        {
            await WriteBlock(chainSegment[i]);
        }
    }

    // Gets all blocks from range ({from}, {to}). It's safe only if {to} is finalized
    private async Task BuildUp(ulong from, ulong to)
    {
        for (ulong i = from + 1; i < to; i++)
        {
            await WriteBlock(await GetBlock(i));
        }
    }

    public void Start()
    {
        _logger.Error($"Starting L1Bridge");
        _headUpdateTask.Start();
    }

    public Task<BlobSidecar[]> GetBlobSidecars(ulong slotNumber, int indexFrom, int indexTo)
    {
        // TODO: retry here
        return _beaconApi.GetBlobSidecars(slotNumber, indexFrom, indexTo)!;
    }

    public async Task<L1Block> GetBlock(ulong blockNumber)
    {
        L1Block? result = await _ethL1Api.GetBlockByNumber(blockNumber, true);
        while (result is null)
        {
            _logger.Warn($"Unable to get L1 block by block number({blockNumber})");
            result = await _ethL1Api.GetBlockByNumber(blockNumber, true);
        }
        CacheBlock(result.Value);
        return result.Value;
    }

    // TODO: pruning
    private readonly ConcurrentDictionary<Hash256, L1Block> _cachedL1Blocks = new();
    private readonly ConcurrentDictionary<Hash256, ReceiptForRpc[]> _cachedReceipts = new();

    private void CacheBlock(L1Block block)
    {
        _cachedL1Blocks.TryAdd(block.Hash, block);
    }

    private void CacheReceipts(Hash256 blockHash, ReceiptForRpc[] receipts)
    {
        _cachedReceipts.TryAdd(blockHash, receipts);
    }

    public async Task<L1Block> GetBlockByHash(Hash256 blockHash)
    {
        if (_cachedL1Blocks.TryGetValue(blockHash, out L1Block cachedBlock))
        {
            return cachedBlock;
        }
        L1Block? result = await _ethL1Api.GetBlockByHash(blockHash, true);
        while (result is null)
        {
            _logger.Warn($"Unable to get L1 block by hash({blockHash})");
            result = await _ethL1Api.GetBlockByHash(blockHash, true);
        }

        CacheBlock(result.Value);
        return result.Value;
    }

    public async Task<ReceiptForRpc[]> GetReceiptsByBlockHash(Hash256 blockHash)
    {
        if (_cachedReceipts.TryGetValue(blockHash, out ReceiptForRpc[]? cachedReceipts))
        {
            return cachedReceipts;
        }
        ReceiptForRpc[]? result = await _ethL1Api.GetReceiptsByHash(blockHash);
        while (result is null)
        {
            _logger.Warn($"Unable to get L1 receipts by hash({blockHash})");
            result = await _ethL1Api.GetReceiptsByHash(blockHash);
        }

        CacheReceipts(blockHash, result);
        return result;
    }

    private async Task<L1Block> GetHead()
    {
        L1Block? result = await _ethL1Api.GetHead(true);
        while (result is null)
        {
            _logger.Warn($"Unable to get L1 head");
            result = await _ethL1Api.GetHead(true);
        }
        CacheBlock(result.Value);
        return result.Value;
    }

    private async Task<L1Block> GetFinalized()
    {
        L1Block? result = await _ethL1Api.GetFinalized(true);
        while (result is null)
        {
            _logger.Warn($"Unable to get finalized L1 block");
            result = await _ethL1Api.GetFinalized(true);
        }
        CacheBlock(result.Value);
        return result.Value;
    }

    public void SetCurrentL1Head(ulong blockNumber, Hash256 blockHash)
    {
        _logger.Error($"Setting current L1 head. {blockNumber}({blockHash})");
        _currentFinalizedNumber = blockNumber;
        _currentHeadNumber = blockNumber;
        _currentHeadHash = blockHash;
    }
}

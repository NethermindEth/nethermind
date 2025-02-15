// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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


    private readonly Channel<(L1Block, ReceiptForRpc[])> NewHeadChannel = Channel.CreateBounded<(L1Block, ReceiptForRpc[])>(20);
    public ChannelReader<(L1Block, ReceiptForRpc[])> NewHeadReader => NewHeadChannel.Reader;


    private async void HeadUpdateLoop()
    {
        while (!_cancellationToken.IsCancellationRequested)
        {
            // TODO: can we do it with subscription?
            L1Block newHead = await GetHead();
            ulong newHeadNumber = newHead.Number;

            if (newHeadNumber > _currentHeadNumber + 1)
            {
                int numberOfMissingBlocks = (int)newHeadNumber - (int)_currentHeadNumber - 1;
                Hash256 currentHash = newHead.ParentHash;
                L1Block[] chainSegment = new L1Block[numberOfMissingBlocks];
                ReceiptForRpc[][] chainSegmentReceipts = new ReceiptForRpc[numberOfMissingBlocks][];
                for (int i = numberOfMissingBlocks - 1; i >= 0; i--)
                {
                    _logger.Info($"Rolling back L1 head. {i} to go. Block {currentHash}");
                    chainSegment[i] = await GetBlockByHash(currentHash);
                    chainSegmentReceipts[i] = await GetReceiptsByBlockHash(currentHash);
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

                for (int i = 0; i < numberOfMissingBlocks; i++)
                {
                    await NewHeadChannel.Writer.WriteAsync((chainSegment[i], chainSegmentReceipts[i]), _cancellationToken);
                    if (_cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                }
            }

            _currentHeadNumber = newHeadNumber;
            _currentHeadHash = newHead.Hash;
            ReceiptForRpc[] receipts = await GetReceiptsByBlockHash(newHead.Hash);
            await NewHeadChannel.Writer.WriteAsync((newHead, receipts!), _cancellationToken);

            await Task.Delay(11000, _cancellationToken);
        }
    }

    public void Start()
    {
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

    public async Task<L1Block> GetHead()
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

    public void SetCurrentL1Head(ulong blockNumber, Hash256 blockHash)
    {
        _currentHeadNumber = blockNumber;
        _currentHeadHash = blockHash;
    }
}

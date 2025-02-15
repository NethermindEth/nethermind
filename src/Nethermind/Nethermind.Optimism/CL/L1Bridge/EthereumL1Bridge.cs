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
            L1Block? currentHead = await _ethL1Api.GetHead(true);
            while (currentHead is null || currentHead.Value.Number <= _currentHeadNumber)
            {
                await Task.Delay(100);
                currentHead = await _ethL1Api.GetHead(true);
            }

            ulong newHeadNumber = (ulong)currentHead.Value.Number;

            for (ulong i = _currentHeadNumber + 1; i < newHeadNumber; i++)
            {
                L1Block? skippedBlock = await _ethL1Api.GetBlockByNumber(i, true);
                ReceiptForRpc[]? skippedReceipts = await _ethL1Api.GetReceiptsByHash(skippedBlock!.Value.Hash);
                await NewHeadChannel.Writer.WriteAsync((skippedBlock.Value, skippedReceipts!), _cancellationToken);
            }
            _currentHeadNumber = newHeadNumber;
            ReceiptForRpc[]? receipts = await _ethL1Api.GetReceiptsByHash(currentHead.Value.Hash);
            await NewHeadChannel.Writer.WriteAsync((currentHead.Value, receipts!), _cancellationToken);

            await Task.Delay(11000, _cancellationToken);
        }
    }

    public void Start()
    {
        _headUpdateTask.Start();
    }

    public Task<BlobSidecar[]?> GetBlobSidecars(ulong slotNumber, int indexFrom, int indexTo)
    {
        return _beaconApi.GetBlobSidecars(slotNumber, indexFrom, indexTo);
    }

    public Task<L1Block?> GetBlock(ulong blockNumber)
    {
        // Do not cache getByNumber
        return _ethL1Api.GetBlockByNumber(blockNumber, true);
    }

    // TODO: pruning
    private readonly ConcurrentDictionary<Hash256, L1Block> _cachedL1Blocks = new();
    private readonly ConcurrentDictionary<Hash256, ReceiptForRpc[]> _cachedReceipts = new();

    public async Task<L1Block?> GetBlockByHash(Hash256 blockHash)
    {
        if (_cachedL1Blocks.TryGetValue(blockHash, out L1Block cachedBlock))
        {
            return cachedBlock;
        }
        L1Block? result = await _ethL1Api.GetBlockByHash(blockHash, true);
        if (result is not null)
        {
            _cachedL1Blocks.TryAdd(blockHash, result.Value);
        }
        return result;
    }

    public async Task<ReceiptForRpc[]?> GetReceiptsByBlockHash(Hash256 blockHash)
    {
        if (_cachedReceipts.TryGetValue(blockHash, out ReceiptForRpc[]? cachedReceipts))
        {
            return cachedReceipts;
        }
        ReceiptForRpc[]? result = await _ethL1Api.GetReceiptsByHash(blockHash);
        if (result is not null)
        {
            _cachedReceipts.TryAdd(blockHash, result);
        }
        return result;
    }

    public void SetCurrentL1Head(ulong blockNumber)
    {
        _currentHeadNumber = blockNumber;
    }
}

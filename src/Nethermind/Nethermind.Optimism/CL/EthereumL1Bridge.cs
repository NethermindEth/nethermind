// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.JsonRpc.Data;
using Nethermind.Logging;

namespace Nethermind.Optimism.CL;

public class EthereumL1Bridge : IL1Bridge
{
    private readonly ICLConfig _config;
    private readonly IEthApi _ethL1Api;
    private readonly IBeaconApi _beaconApi;
    private readonly Task _headUpdateTask;
    private readonly ILogger _logger;
    private readonly CancellationToken _cancellationToken;

    private ulong _currentSlot = 0;

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

    private async void HeadUpdateLoop()
    {
        while (!_cancellationToken.IsCancellationRequested)
        {
            // TODO: can we do it with subscription?
            BeaconBlock beaconBlock = await _beaconApi.GetHead();
            while (beaconBlock.SlotNumber <= _currentSlot)
            {
                await Task.Delay(100);
                beaconBlock = await _beaconApi.GetHead();
            }

            // TODO: handle missed slots(_currentSlot + 1 < beaconBlock.SlotNumber)
            _logger.Error($"HEAD UPDATED: slot {beaconBlock.SlotNumber}");
            // new slot
            _currentSlot = beaconBlock.SlotNumber;

            ReceiptForRpc[]? receipts = await _ethL1Api.GetReceiptsByHash(beaconBlock.ExecutionBlockHash);
            OnNewL1Head?.Invoke(beaconBlock, receipts!);

            // Wait next slot
            await Task.Delay(11000, _cancellationToken);
        }
    }

    public void Start()
    {
        // var res = await _beaconApi.GetHead();
        // await _beaconApi.GetBlobSidecars(res.SlotNumber);
        _headUpdateTask.Start();
    }

    public event Action<BeaconBlock, ReceiptForRpc[]>? OnNewL1Head;

    public Task<BlobSidecar[]> GetBlobSidecars(ulong slotNumber)
    {
        return _beaconApi.GetBlobSidecars(slotNumber);
    }
}

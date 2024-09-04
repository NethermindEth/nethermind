// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Facade.Eth;
using Nethermind.Logging;

namespace Nethermind.Optimism.CL;

public class EthereumL1Bridge : IL1Bridge
{
    private readonly ICLConfig _config;
    private readonly IEthApi _ethL1Api;
    private readonly IBeaconApi _beaconApi;
    private readonly Task _headUpdateTask;
    private readonly ILogger _logger;

    private ulong _currentSlot = 0;

    public EthereumL1Bridge(IEthApi ethL1Rpc, IBeaconApi beaconApi, ICLConfig config, ILogManager logManager)
    {
        _logger = logManager.GetClassLogger();
        _config = config;
        _ethL1Api = ethL1Rpc;
        _beaconApi = beaconApi;
        _headUpdateTask = new Task(() =>
        {
            HeadUpdateLoop();
        });
    }

    private async void HeadUpdateLoop()
    {
        // TODO: Cancellation token
        while (true)
        {
            // TODO: can we do it with subscription?
            BeaconBlock beaconBlock = await _beaconApi.GetHead();
            while (beaconBlock.SlotNumber <= _currentSlot)
            {
                await Task.Delay(100);
                beaconBlock = await _beaconApi.GetHead();
            }

            _logger.Error($"HEAD UPDATED: slot {beaconBlock.SlotNumber}");
            // new slot
            _currentSlot = beaconBlock.SlotNumber;
            // BlockForRpc? block = await _ethL1Api.GetBlockByNumber(beaconBlock.PayloadNumber);

            // if (block is null)
            // {
            //     if (_logger.IsError) _logger.Error($"Unable to get L1 block");
            //     return;
            // }

            OnNewL1Head?.Invoke(beaconBlock, _currentSlot);

            // Wait next slot
            await Task.Delay(12000);
        }
    }

    public void Start()
    {
        // var res = await _beaconApi.GetHead();
        // await _beaconApi.GetBlobSidecars(res.SlotNumber);
        _headUpdateTask.Start();
    }

    public event Action<BeaconBlock, ulong>? OnNewL1Head;

    public Task<BlobSidecar[]> GetBlobSidecars(ulong slotNumber)
    {
        return _beaconApi.GetBlobSidecars(slotNumber);
    }
}

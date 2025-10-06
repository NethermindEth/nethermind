// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Common.Utilities;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Nethermind.Xdc;
internal class XdcFlowManager(
    IBlockTree blockTree,
    ISpecProvider specProvider,
    IBlockProducer blockBuilder,
    IEpochSwitchManager epochSwitchManager,
    IQuorumCertificateManager quorumCertificateManager,
    ITimeoutCertificateManager timeoutCertificateManager,
    ILogManager logManager) : IBlockProducerRunner
{
    public event EventHandler<BlockEventArgs> BlockProduced;
    private RoundCount _roundCount;
    private CancellationTokenSource _cancellationTokenSource = new();
    private bool _isRunning = false;
    private Task _mainFlowTask;
    private readonly Channel<RoundSignal> _newRoundSignals = Channel.CreateBounded<RoundSignal>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        SingleWriter = false
    });

    private ILogger _logger = logManager.GetClassLogger();

    public bool IsProducingBlocks(ulong? maxProducingInterval)
    {
        throw new NotImplementedException();
    }

    public void Start()
    {
        if (_isRunning)
            return;
        _isRunning = true;
        //Return control right away
        _mainFlowTask = Task.Run(Run);
    }

    private async Task Run()
    {
        while (blockTree.Head is null)
        {
            //Need to wait in case Head is not yet initialized
            await Task.Delay(200);
        }
        blockTree.NewHeadBlock += OnNewHeadBlock;

        try
        {
            await MainFlow();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            _logger.Error("XdcFlowManager crashed", e);
            throw;
        }
    }

    private async Task MainFlow()
    {
        //TODO what do we do when we are syncing?
        await foreach (RoundSignal newRoundSignal in _newRoundSignals.Reader.ReadAllAsync(_cancellationTokenSource.Token))
        {
            XdcBlockHeader currentHead = newRoundSignal.CurrentHead ?? (XdcBlockHeader)blockTree.Head.Header;
            //TODO this is not the right way to get the current round
            IXdcReleaseSpec spec = specProvider.GetXdcSpec(currentHead, currentHead.ExtraConsensusData.CurrentRound);

            //TODO Technically we have to apply timeout exponents from spec, but they are always 1
            Task timeoutTask = Task.Delay(TimeSpan.FromSeconds(spec.TimeoutPeriod));

            //TODO make sure epoch switch is handled correctly
            EpochSwitchInfo? epochSwitchInfo = epochSwitchManager.GetEpochSwitchInfo(currentHead, currentHead.ParentHash);

            if (newRoundSignal.CurrentHead is not null)
            {
                //Start block production for the new block
                Block myProposed = blockBuilder.BuildBlock(currentHead, epochSwitchInfo, spec);

            }
            else
            {
                _logger.Error("Received empty round signal");
            }
        }
    }

    private bool IsMyTurn(XdcBlockHeader currentHead, Address[] masterNodes, long currentRound, IXdcReleaseSpec spec)
    {
        EpochSwitchInfo epochSwitchInfo = null;
        if (epochSwitchManager.IsEpochSwitchAtRound((ulong)currentRound, currentHead, out _))
        {
            //TODO calculate master nodes based on the current round
            
        }
        else
        {
            epochSwitchInfo = epochSwitchManager.GetEpochSwitchInfo(currentHead, null);
        }



    }

    private bool IsNextEpochSwitch(XdcBlockHeader currentHead, long currentRound, IXdcReleaseSpec spec)
    {
        //First V2 block counts as an epoch switch
        if (currentHead.Number == spec.SwitchBlock)
            return true;

        long lastRound = (long)currentHead.ExtraConsensusData.CurrentRound;
        if (currentRound <= lastRound)
        {
            //This should never happen
            _logger.Warn($"The current round {currentRound} is not greater than round {lastRound} from current head block.");
            return false;
        }
        var epochStartRound = currentRound - currentRound % spec.EpochLength;
        return lastRound < epochStartRound;
    }


    private void OnNewHeadBlock(object? sender, BlockEventArgs e)
    {
        StartNewRound();
        _newRoundSignals.Writer.TryWrite(new RoundSignal((XdcBlockHeader)e.Block.Header, _roundCount.CurrentRound));
    }

    private void StartNewRound()
    {
        DateTime lastRoundStart = _roundCount.CurrentRoundStartTime;
        long lastRound = _roundCount.CurrentRound;
        _roundCount.StartNewRound();
        if (_logger.IsInfo)
        {
            _logger.Info($"Current round {lastRound} finished in {_roundCount.CurrentRoundStartTime - lastRoundStart}");
            _logger.Info($"Starting next round { _roundCount.CurrentRound }");
        }
    }

    public Task StopAsync()
    {
        blockTree.NewHeadBlock -= OnNewHeadBlock;

        _cancellationTokenSource.Cancel();
        _isRunning = false;
        return Task.CompletedTask;
    }

    private class RoundSignal
    {
        public RoundSignal(XdcBlockHeader block, long round)
        {
            this.CurrentHead = block;
            this.CurrentRound = round;
        }

        public XdcBlockHeader CurrentHead { get; }
        public long CurrentRound { get; }
    }

    private class RoundCount(long startingRound)
    {
        public long CurrentRound { get; private set; } = startingRound;
        public DateTime CurrentRoundStartTime { get; private set; } = DateTime.UtcNow;

        public void StartNewRound()
        {
            CurrentRound++;
            CurrentRoundStartTime = DateTime.UtcNow;
        }
    }

}

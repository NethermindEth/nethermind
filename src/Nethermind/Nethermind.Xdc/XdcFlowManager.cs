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
internal class XdcFlowManager(IBlockTree blockTree, ISpecProvider specProvider, IQuorumCertificateManager quorumCertificateManager, ITimeoutCertificateManager timeoutCertificateManager, ILogManager logManager) : IBlockProducerRunner
{
    public event EventHandler<BlockEventArgs> BlockProduced;
    private RoundCount _roundCount;
    private CancellationTokenSource _cancellationTokenSource = new();
    private bool _isRunning = false;
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
        Task.Run(InternalStart);
    }

    private async Task InternalStart()
    {
        while (blockTree.Head is null)
        {
            //Need to wait in case Head is not yet initialized
            await Task.Delay(200);
        }
        blockTree.NewHeadBlock += OnNewHeadBlock;


    }

    private async Task MainFlow()
    {
        //TODO what do we do when we are syncing?
        await foreach (RoundSignal newRoundSignal in _newRoundSignals.Reader.ReadAllAsync(_cancellationTokenSource.Token))
        {
            XdcBlockHeader currentHead = newRoundSignal.NewHeader ?? (XdcBlockHeader)blockTree.Head.Header;
            //TODO this is not the right way to get the correct round
            IXdcReleaseSpec spec = specProvider.GetXdcSpec(currentHead, currentHead.ExtraConsensusData.CurrentRound);
            if (newRoundSignal.NewHeader is not null)
            {
                //Start block production for the new block


            }
            else if (newRoundSignal.TimeoutCertificate is not null)
            {

            }
            else
            {
                _logger.Error("Received empty round signal");
            }
        }
    }

    private void OnNewHeadBlock(object? sender, BlockEventArgs e)
    {
        StartNewRound();
        _newRoundSignals.Writer.TryWrite(new RoundSignal((XdcBlockHeader)e.Block.Header));
    }

    private void StartNewRound()
    {
        _roundCount.StartNewRound();
        if (_logger.IsInfo)
            _logger.Info($"Starting new round {_roundCount.CurrentRound} at {_roundCount.CurrentRoundStartTime}");


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
        public RoundSignal(XdcBlockHeader block)
        {
            this.NewHeader = block;
        }
        public RoundSignal(TimeoutCert timeoutCert)
        {
            this.TimeoutCertificate = timeoutCert;
        }

        public XdcBlockHeader? NewHeader { get; }
        public TimeoutCert? TimeoutCertificate { get; }
    }

    private class RoundCount(long startingRound)
    {
        public long CurrentRound { get; private set; } = startingRound;
        public DateTime CurrentRoundStartTime { get; private set; } = DateTime.Now;

        public void StartNewRound()
        {
            CurrentRound++;
            CurrentRoundStartTime = DateTime.Now;
        }
    }

}

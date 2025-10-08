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
internal class XdcHotStuff(
    IBlockTree blockTree,
    ISpecProvider specProvider,
    IBlockProducer blockBuilder,
    IEpochSwitchManager epochSwitchManager,
    IQuorumCertificateManager quorumCertificateManager,
    ITimeoutCertificateManager timeoutCertificateManager,
    IVotesManager votesManager,
    ISigner signer,
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
        //P2P new block -> BlockValidator.ValidateSuggestedBlock -> Block is processed and set as head



        //TODO what do we do when we are syncing?

        //TODO what if we restart and the current round is far ahead of the last block?

        //
        await foreach (RoundSignal newRoundSignal in _newRoundSignals.Reader.ReadAllAsync(_cancellationTokenSource.Token))
        {
            XdcBlockHeader currentHead = newRoundSignal.NewBlock ?? (XdcBlockHeader)blockTree.Head.Header;
            XdcBlockHeader parent = (XdcBlockHeader)blockTree.FindHeader(currentHead.ParentHash);

            //TODO this is not the right way to get the current round
            IXdcReleaseSpec spec = specProvider.GetXdcSpec(currentHead, currentHead.ExtraConsensusData.BlockRound);
            
            //TODO Technically we have to apply timeout exponents from spec, but they are always 1
            Task timeoutTask = Task.Delay(TimeSpan.FromSeconds(spec.TimeoutPeriod));

            //TODO make sure epoch switch is handled correctly inside the manager
            EpochSwitchInfo? epochSwitchInfo = epochSwitchManager.GetEpochSwitchInfo(currentHead, currentHead.ParentHash);

            if (IsMyTurn(currentHead, epochSwitchInfo.Masternodes, newRoundSignal.CurrentRound, spec))
            {
                //TODO Check numbers here, in case we get an old or block with a future timestamp
                //Next block must have a timestamp after current head + mine period
                TimeSpan minimumMiningTime = DateTimeOffset.FromUnixTimeSeconds((long)currentHead.Timestamp + spec.MinePeriod) - DateTimeOffset.UtcNow;

                
                //If its my turn produce a block and broadcast after minimum wait time
                //This should be done by PayloadPreparationService
                Task<Block> blockProduction = blockBuilder.BuildBlock(currentHead, null, null, IBlockProducer.Flags.None, _cancellationTokenSource.Token);
            }

            quorumCertificateManager.CommitCertificate(currentHead.ExtraConsensusData.QuorumCert);

            if (!epochSwitchInfo.Masternodes.Contains(signer.Address))
            {
                _logger.Info($"Skipping voting in round {_roundCount.CurrentRound} since not a masternode");
                continue;
            }

            if (!quorumCertificateManager.VerifyVotingRule(currentHead))
            {
                _logger.Info($"Cannot vote on current head {currentHead.ToString(BlockHeader.Format.Short)}");
                continue;
            }

            Task voteTask = votesManager.CastVote(new BlockRoundInfo(currentHead.Hash, currentHead.ExtraConsensusData.BlockRound, currentHead.Number));

            //Handle own vote here
            //votesManager.HandleVote();


        }
    }
    private Task WaitForSignal()
    {

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
        
        int myIndex = Array.IndexOf(epochSwitchInfo.Masternodes, signer.Address);
        if (myIndex < 0)
            return false;

        int currentLeaderIndex = (int)(currentRound % spec.EpochLength % epochSwitchInfo.Masternodes.Length);

        return myIndex == currentLeaderIndex;
    }

    private bool IsNextEpochSwitch(XdcBlockHeader currentHead, long currentRound, IXdcReleaseSpec spec)
    {
        //First V2 block counts as an epoch switch
        if (currentHead.Number == spec.SwitchBlock)
            return true;

        long lastRound = (long)currentHead.ExtraConsensusData.BlockRound;
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
            this.NewBlock = block;
            this.CurrentRound = round;
        }

        public XdcBlockHeader NewBlock { get; }
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

        public void SetRound(long round)
        {
            CurrentRound = round;
            CurrentRoundStartTime = DateTime.UtcNow;
        }   
    }

}

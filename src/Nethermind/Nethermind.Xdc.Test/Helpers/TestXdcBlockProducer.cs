// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.Xdc.Spec;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Nethermind.Xdc.Test.Helpers;

internal class TestXdcBlockProducer(
    ISigner signer,
    CandidateContainer candidateContainer,
    IEpochSwitchManager epochSwitchManager,
    ISnapshotManager snapshotManager,
    IXdcConsensusContext xdcContext,
    ITxSource txSource,
    IBlockchainProcessor processor,
    ISealer sealer,
    IBlockTree blockTree,
    IWorldState stateProvider,
    IGasLimitCalculator? gasLimitCalculator,
    ITimestamper? timestamper,
    ISpecProvider specProvider,
    ILogManager logManager,
    IDifficultyCalculator? difficultyCalculator,
    IBlocksConfig? blocksConfig) : XdcBlockProducer(epochSwitchManager, snapshotManager, xdcContext, txSource, processor, sealer, blockTree, stateProvider, gasLimitCalculator, timestamper, specProvider, logManager, difficultyCalculator, blocksConfig)
{
    private readonly Signer signer = (Signer)signer;

    public BlockHeader WrappedPrepare(BlockHeader parent, PayloadAttributes payloadAttributes)
    {
        return PrepareBlockHeader(parent, payloadAttributes);
    }

    protected override BlockHeader PrepareBlockHeader(BlockHeader parent, PayloadAttributes payloadAttributes)
    {
        if (parent is not XdcBlockHeader xdcParent)
            throw new ArgumentException($"Must be a {nameof(XdcBlockHeader)}", nameof(parent));
        var prepared = (XdcBlockHeader)base.PrepareBlockHeader(parent, payloadAttributes);

        IXdcReleaseSpec headSpec = _specProvider.GetXdcSpec(prepared, xdcContext.CurrentRound);
        var leader = GetLeaderAddress(xdcParent, xdcContext.CurrentRound, headSpec);
        signer.SetSigner(candidateContainer.MasternodeCandidates.First(k => k.Address == leader));
        prepared.Beneficiary = leader;
        return prepared;
    }

    private Address GetLeaderAddress(XdcBlockHeader currentHead, ulong round, IXdcReleaseSpec spec)
    {
        Address[] masternodes;
        if (epochSwitchManager.IsEpochSwitchAtRound(round, currentHead))
        {
            //TODO calculate master nodes based on the current round
            (masternodes, _) = snapshotManager.CalculateNextEpochMasternodes(currentHead.Number + 1, currentHead.Hash!, spec);
        }
        else
        {
            var epochSwitchInfo = epochSwitchManager.GetEpochSwitchInfo(currentHead);
            masternodes = epochSwitchInfo!.Masternodes;
        }

        int currentLeaderIndex = ((int)round % spec.EpochLength % masternodes.Length);
        return masternodes[currentLeaderIndex];
    }
}

internal class CandidateContainer(List<PrivateKey> candidates)
{
    public List<PrivateKey> MasternodeCandidates { get; set; } = candidates;
}

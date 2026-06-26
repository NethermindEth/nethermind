// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Xdc.Spec;
using System;

namespace Nethermind.Xdc;

internal class XdcSubnetBlockProducer(
    IEpochSwitchManager epochSwitchManager,
    ISubnetMasternodesCalculator subnetMasternodesCalculator,
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
    IBlocksConfig? blocksConfig) : XdcBlockProducer(epochSwitchManager, subnetMasternodesCalculator, xdcContext, txSource, processor, sealer, blockTree, stateProvider, gasLimitCalculator, timestamper, specProvider, logManager, difficultyCalculator, blocksConfig)
{
    protected override BlockHeader PrepareBlockHeader(BlockHeader parent, PayloadAttributes? payloadAttributes)
    {
        if (base.PrepareBlockHeader(parent, payloadAttributes) is not XdcSubnetBlockHeader headerCandidate)
            throw new InvalidOperationException("PrepareBlockHeader did not return XdcSubnetBlockHeader");
        //Penalties are not set on epoch switch, but on gap+1
        headerCandidate.Penalties = [];

        IXdcReleaseSpec spec = specProvider.GetXdcSpec(headerCandidate);
        if (headerCandidate.IsGapPlusOne(spec))
        {
            Hash256 parentHash = headerCandidate.ParentHash
                ?? throw new InvalidOperationException($"Parent hash is missing for block {headerCandidate.Number}.");
            (Address[] nextEpochCandidates, Address[] nextPenalties) = subnetMasternodesCalculator.GetNextEpochCandidatesAndPenalties(parentHash);
            headerCandidate.NextValidators = new byte[nextEpochCandidates.Length * Address.Size];
            for (int i = 0; i < nextEpochCandidates.Length; i++)
            {
                nextEpochCandidates[i].Bytes.CopyTo(headerCandidate.NextValidators.AsSpan(i * Address.Size, Address.Size));
            }

            headerCandidate.Penalties = new byte[nextPenalties.Length * Address.Size];
            for (int i = 0; i < nextPenalties.Length; i++)
            {
                nextPenalties[i].Bytes.CopyTo(headerCandidate.Penalties.AsSpan(i * Address.Size, Address.Size));
            }
        }

        return headerCandidate;
    }

    protected override XdcBlockHeader CreateHeader(BlockHeader parent, byte[] extra, Address blockAuthor, ulong gasLimit) => new XdcSubnetBlockHeader(
                parent.Hash!,
                Keccak.OfAnEmptySequenceRlp,
                blockAuthor,
                UInt256.Zero,
                parent.Number + 1,
                gasLimit,
                0,
                extra,
                isSelfMined: true);
}

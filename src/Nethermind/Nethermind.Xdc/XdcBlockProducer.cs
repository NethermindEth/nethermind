// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
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
using Nethermind.Xdc.RLP;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using System;

namespace Nethermind.Xdc;

internal class XdcBlockProducer : BlockProducerBase
{
    protected readonly IEpochSwitchManager epochSwitchManager;
    protected readonly ISnapshotManager snapshotManager;
    protected readonly IMasternodesCalculator masternodesCalculator;
    protected readonly IXdcConsensusContext xdcContext;
    protected readonly ISealer sealer;
    protected readonly ISpecProvider specProvider;
    private static readonly ExtraConsensusDataDecoder _extraConsensusDataDecoder = new();

    public XdcBlockProducer(IEpochSwitchManager epochSwitchManager, ISnapshotManager snapshotManager, IMasternodesCalculator masternodesCalculator, IXdcConsensusContext xdcContext, ITxSource txSource, IBlockchainProcessor processor, ISealer sealer, IBlockTree blockTree, IWorldState stateProvider, IGasLimitCalculator? gasLimitCalculator, ITimestamper? timestamper, ISpecProvider specProvider, ILogManager logManager, IDifficultyCalculator? difficultyCalculator, IBlocksConfig? blocksConfig) : base(txSource, processor, sealer, blockTree, stateProvider, gasLimitCalculator, timestamper, specProvider, logManager, difficultyCalculator, blocksConfig)
    {
        this.epochSwitchManager = epochSwitchManager;
        this.snapshotManager = snapshotManager;
        this.masternodesCalculator = masternodesCalculator;
        this.xdcContext = xdcContext;
        this.sealer = sealer;
        this.specProvider = specProvider;
    }

    protected override BlockHeader PrepareBlockHeader(BlockHeader parent, PayloadAttributes payloadAttributes)
    {
        if (parent is not XdcBlockHeader xdcParent)
            throw new ArgumentException("Only XDC header are supported.");

        QuorumCertificate highestCert = xdcContext.HighestQC;
        var currentRound = xdcContext.CurrentRound;

        //TODO maybe some sanity checks here for round and hash

        byte[] extra = [XdcConstants.ConsensusVersion, .. _extraConsensusDataDecoder.Encode(new ExtraFieldsV2(currentRound, highestCert)).Bytes];

        Address blockAuthor = sealer.Address;
        XdcBlockHeader xdcBlockHeader = new(
            parent.Hash!,
            Keccak.OfAnEmptySequenceRlp,
            blockAuthor,
            UInt256.Zero,
            parent.Number + 1,
            //This should probably use TargetAdjustedGasLimitCalculator
            XdcConstants.TargetGasLimit,
            0,
            extra)
        {
            //This will BestSuggestedBody in BlockTree, which may not be needed
            IsPostMerge = true
        };

        IXdcReleaseSpec spec = specProvider.GetXdcSpec(xdcBlockHeader, currentRound);

        xdcBlockHeader.Timestamp = payloadAttributes?.Timestamp ?? parent.Timestamp + (ulong)spec.MinePeriod;

        xdcBlockHeader.Difficulty = 1;

        xdcBlockHeader.TotalDifficulty = xdcParent.TotalDifficulty + 1;

        xdcBlockHeader.BaseFeePerGas = BaseFeeCalculator.Calculate(parent, spec);

        xdcBlockHeader.MixHash = Hash256.Zero;

        if (epochSwitchManager.IsEpochSwitchAtBlock(xdcBlockHeader))
        {
            (Address[] masternodes, Address[] penalties) = masternodesCalculator.CalculateNextEpochMasternodes(xdcBlockHeader.Number, xdcBlockHeader.ParentHash, spec);
            xdcBlockHeader.Validators = new byte[masternodes.Length * Address.Size];

            for (int i = 0; i < masternodes.Length; i++)
            {
                Array.Copy(masternodes[i].Bytes, 0, xdcBlockHeader.Validators, i * Address.Size, Address.Size);
            }

            xdcBlockHeader.Penalties = new byte[penalties.Length * Address.Size];

            for (int i = 0; i < penalties.Length; i++)
            {
                Array.Copy(penalties[i].Bytes, 0, xdcBlockHeader.Penalties, i * Address.Size, Address.Size);
            }
        }
        return xdcBlockHeader;
    }
}

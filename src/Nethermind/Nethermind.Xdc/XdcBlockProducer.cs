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
using Nethermind.Xdc.RLP;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using System;
using System.Collections.Generic;

namespace Nethermind.Xdc;

internal class XdcBlockProducer(
    IEpochSwitchManager epochSwitchManager,
    IMasternodesCalculator masternodesCalculator,
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
    IBlocksConfig? blocksConfig) : BlockProducerBase(txSource, processor, sealer, blockTree, stateProvider, gasLimitCalculator, timestamper, specProvider, logManager, difficultyCalculator, blocksConfig)
{
    protected readonly IEpochSwitchManager epochSwitchManager = epochSwitchManager;
    protected readonly IMasternodesCalculator masternodesCalculator = masternodesCalculator;
    protected readonly IXdcConsensusContext xdcContext = xdcContext;
    protected readonly ISealer sealer = sealer;
    protected readonly ISpecProvider specProvider = specProvider;
    private static readonly ExtraConsensusDataDecoder _extraConsensusDataDecoder = new();

    protected override BlockHeader PrepareBlockHeader(BlockHeader parent, PayloadAttributes payloadAttributes)
    {
        if (parent is not XdcBlockHeader xdcParent)
            throw new ArgumentException("Only XDC header are supported.");

        QuorumCertificate highestCert = xdcContext.HighestQC;
        ulong currentRound = xdcContext.CurrentRound;

        if (payloadAttributes is XdcPayloadAttributes xdcPayloadAttributes)
        {
            currentRound = xdcPayloadAttributes.Round;
            highestCert = xdcPayloadAttributes.QuorumCertificate;
        }

        //TODO maybe some sanity checks here for round and hash

        byte[] extra = [XdcConstants.ConsensusVersion, .. _extraConsensusDataDecoder.Encode(new ExtraFieldsV2(currentRound, highestCert)).Bytes];

        Address blockAuthor = sealer.Address;
        long gasLimit = GasLimitCalculator.GetGasLimit(parent);
        XdcBlockHeader xdcBlockHeader = CreateHeader(parent, extra, blockAuthor, gasLimit);

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

    protected override BlockToProduce PrepareBlock(BlockHeader parent, PayloadAttributes? payloadAttributes = null, IBlockProducer.Flags flags = IBlockProducer.Flags.None)
    {
        BlockHeader header = PrepareBlockHeader(parent, payloadAttributes);

        IEnumerable<Transaction> transactions = (flags & IBlockProducer.Flags.EmptyBlock) != 0 ?
            Array.Empty<Transaction>() :
            TxSource.GetTransactions(parent, header.GasLimit, payloadAttributes, filterSource: false);

        return new BlockToProduce(header, transactions, Array.Empty<BlockHeader>(), payloadAttributes?.Withdrawals);
    }

    protected virtual XdcBlockHeader CreateHeader(BlockHeader parent, byte[] extra, Address blockAuthor, long gasLimit) => new(
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

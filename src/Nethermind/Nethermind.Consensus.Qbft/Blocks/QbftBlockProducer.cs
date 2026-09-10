// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Consensus.Qbft.Config;
using Nethermind.Consensus.Qbft.StateMachine;
using Nethermind.Consensus.Qbft.Validators;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.Consensus.Qbft.Blocks;

/// <summary>Round-specific inputs for building a QBFT proposal.</summary>
public sealed class QbftPayloadAttributes : PayloadAttributes
{
    public int Round { get; init; }
}

/// <summary>
/// Builds unsealed proposal blocks: proposer as coinbase, difficulty one, the BFT mix hash and an
/// extra data carrying the vanity, the validator list, the local node's vote and the round.
/// </summary>
/// <remarks>
/// The block is executed but not sealed; committed seals are added by the state machine once a
/// commit quorum exists. Mirrors Besu's <c>BftBlockCreator</c> + <c>QbftBlockCreatorFactory</c>.
/// </remarks>
public sealed class QbftBlockProducer(
    ITxSource txSource,
    IBlockchainProcessor processor,
    ISealer sealer,
    IBlockTree blockTree,
    IWorldState stateProvider,
    IGasLimitCalculator gasLimitCalculator,
    ITimestamper timestamper,
    ISpecProvider specProvider,
    IBlocksConfig blocksConfig,
    IValidatorProvider validatorProvider,
    QbftForksSchedule forksSchedule,
    IBftExtraDataCodecSelector codecs,
    ILogManager logManager)
    : BlockProducerBase(txSource, processor, sealer, blockTree, stateProvider, gasLimitCalculator, timestamper, specProvider, logManager, ConstantDifficulty.One, blocksConfig)
{
    protected override BlockHeader PrepareBlockHeader(BlockHeader parent, PayloadAttributes? payloadAttributes = null)
    {
        int round = (payloadAttributes as QbftPayloadAttributes)?.Round ?? 0;
        PayloadAttributes attributes = payloadAttributes ?? throw new ArgumentNullException(nameof(payloadAttributes), "QBFT proposals need the round timestamp.");
        IReleaseSpec spec = _specProvider.GetSpec(parent.Number + 1, attributes.Timestamp);
        attributes.SuggestedFeeRecipient = Sealer.Address;
        attributes.PrevRandao = BftHelpers.ExpectedMixHash;
        attributes.Withdrawals ??= spec.WithdrawalsEnabled ? [] : null;
        attributes.ParentBeaconBlockRoot ??= spec.IsBeaconBlockRootAvailable ? Keccak.Zero : null;

        BlockHeader header = base.PrepareBlockHeader(parent, attributes);
        QbftConfigSnapshot config = forksSchedule.GetFork((long)header.Number, header.Timestamp);
        QbftBlockHeader qbftHeader = QbftBlockHeader.UpgradeFrom(header, codecs.ForBlock(header.Number));
        qbftHeader.Beneficiary = Sealer.Address;
        // Fees go to the configured beneficiary when there is one; the coinbase stays the proposer.
        qbftHeader.Author = config.MiningBeneficiary ?? Sealer.Address;
        qbftHeader.Nonce = 0;
        qbftHeader.Difficulty = UInt256.One;
        qbftHeader.TotalDifficulty = parent.TotalDifficulty + UInt256.One;
        qbftHeader.ExtraData = CreateExtraData(parent, round, config, qbftHeader.Codec);
        return qbftHeader;
    }

    private byte[] CreateExtraData(BlockHeader parent, int round, QbftConfigSnapshot config, IBftExtraDataCodec codec)
    {
        byte[] vanity = ZeroLeftPad(_blocksConfig.GetExtraDataBytes(), BftExtraData.VanityLength);
        if (config.IsValidatorContractMode)
        {
            // Validators and votes come from the contract, so the header carries neither.
            return codec.Encode(new BftExtraData(vanity, [], null, round, []));
        }

        IVoteProvider voteProvider = validatorProvider.GetVoteProviderAfterBlock(parent) ?? throw new InvalidOperationException("BFT requires a vote provider in block header mode.");
        ValidatorVote? proposal = voteProvider.GetVoteAfterBlock(parent, Sealer.Address);
        Vote? vote = proposal is null ? null : new Vote(proposal.Recipient, proposal.Type);
        Address[] validators = [.. validatorProvider.GetValidatorsAfterBlock(parent)];
        Array.Sort(validators);
        return codec.Encode(new BftExtraData(vanity, [], vote, round, validators));
    }

    /// <summary>Besu's <c>ConsensusHelpers.zeroLeftPad</c>: pad on the left to the length, or keep the first bytes when longer.</summary>
    public static byte[] ZeroLeftPad(ReadOnlySpan<byte> input, int requiredLength)
    {
        byte[] result = new byte[requiredLength];
        int padding = Math.Max(0, requiredLength - input.Length);
        input[..Math.Min(input.Length, requiredLength - padding)].CopyTo(result.AsSpan(padding));
        return result;
    }

    /// <summary>Synchronous entry for the consensus thread: builds and executes the proposal for <paramref name="round"/> without sealing it.</summary>
    public Block? CreateProposalBlock(BlockHeader parent, ulong timestampSeconds, int round, CancellationToken cancellationToken = default)
    {
        QbftPayloadAttributes attributes = new() { Timestamp = timestampSeconds, Round = round };
        return BuildBlock(parent, null, attributes, IBlockProducer.Flags.DontSeal, cancellationToken).GetAwaiter().GetResult();
    }
}

/// <summary>Adapts <see cref="QbftBlockProducer"/> to the state machine's per-round block creator.</summary>
public sealed class QbftBlockCreatorFactory(QbftBlockProducer producer) : IQbftBlockCreatorFactory
{
    public IQbftBlockCreator Create(int roundNumber) => new QbftBlockCreator(producer, roundNumber);

    private sealed class QbftBlockCreator(QbftBlockProducer producer, int round) : IQbftBlockCreator
    {
        public BlockCreationResult CreateBlock(ulong headerTimestampSeconds, BlockHeader parentHeader)
        {
            Block block = producer.CreateProposalBlock(parentHeader, headerTimestampSeconds, round)
                          ?? throw new InvalidOperationException($"Failed to build proposal block on top of {parentHeader.ToString(BlockHeader.Format.Short)}.");
            return new BlockCreationResult(block, block.BlockAccessList);
        }
    }
}

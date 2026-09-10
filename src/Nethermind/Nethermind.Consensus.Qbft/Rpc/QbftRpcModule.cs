// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Consensus.Qbft.Config;
using Nethermind.Consensus.Qbft.Contracts;
using Nethermind.Consensus.Qbft.P2P;
using Nethermind.Consensus.Qbft.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.JsonRpc;

namespace Nethermind.Consensus.Qbft.Rpc;

/// <summary>Besu's <c>QBFT</c> JSON-RPC group plus the Nethermind extensions.</summary>
/// <remarks>
/// Reads go through a dedicated block-header validator provider with its own tally cache so that RPC
/// queries on old blocks do not evict the entries consensus relies on (Besu does the same).
/// </remarks>
public sealed class QbftRpcModule(
    IBlockTree blockTree,
    IValidatorProvider validatorProvider,
    IValidatorContract validatorContract,
    EpochManager epochManager,
    BftBlockInterface blockInterface,
    BftForksSchedule forksSchedule,
    QbftChainSpecEngineParameters parameters,
    ISigner signer,
    ValidatorPeers validatorPeers,
    QbftConsensusStatus status) : IQbftRpcModule
{
    /// <inheritdoc cref="BftValidatorRpc.MethodNotEnabledErrorCode"/>
    public const int MethodNotEnabledErrorCode = BftValidatorRpc.MethodNotEnabledErrorCode;

    private readonly IBlockTree _blockTree = blockTree;
    private readonly IValidatorProvider _validatorProvider = validatorProvider;
    private readonly BftValidatorRpc _bft = new(
        blockTree,
        validatorProvider,
        new ForkingValidatorProvider(
            blockTree,
            forksSchedule,
            BlockValidatorProvider.NonForking(blockTree, epochManager, blockInterface),
            new TransactionValidatorProvider(blockTree, validatorContract, forksSchedule)));
    private readonly BftBlockInterface _blockInterface = blockInterface;
    private readonly BftForksSchedule _forksSchedule = forksSchedule;
    private readonly QbftChainSpecEngineParameters _parameters = parameters;
    private readonly ISigner _signer = signer;
    private readonly ValidatorPeers _validatorPeers = validatorPeers;
    private readonly QbftConsensusStatus _status = status;

    public ResultWrapper<bool> qbft_discardValidatorVote(Address validatorAddress) => _bft.DiscardValidatorVote(validatorAddress);

    public ResultWrapper<IReadOnlyDictionary<Address, bool>> qbft_getPendingVotes() => _bft.GetPendingVotes();

    public ResultWrapper<bool> qbft_proposeValidatorVote(Address validatorAddress, bool add) => _bft.ProposeValidatorVote(validatorAddress, add);

    public ResultWrapper<int> qbft_getRequestTimeoutSeconds() => ResultWrapper<int>.Success(_parameters.RequestTimeoutSeconds);

    public ResultWrapper<Address[]?> qbft_getValidatorsByBlockHash(Hash256 blockHash) => _bft.GetValidatorsByBlockHash(blockHash);

    public ResultWrapper<Address[]?> qbft_getValidatorsByBlockNumber(BlockParameter blockParameter) => _bft.GetValidatorsByBlockNumber(blockParameter);

    public ResultWrapper<SignerMetricResult[]> qbft_getSignerMetrics(BlockParameter? fromBlock = null, BlockParameter? toBlock = null) =>
        _bft.GetSignerMetrics(fromBlock, toBlock);

    public ResultWrapper<QbftBlockSealInfo?> qbft_getBlockCommitters(BlockParameter blockParameter)
    {
        BlockHeader? header = _blockTree.FindHeader(blockParameter);
        if (header is null)
        {
            return ResultWrapper<QbftBlockSealInfo?>.Success(null);
        }

        BftExtraData extraData;
        try
        {
            extraData = _blockInterface.GetExtraData(header);
        }
        catch (Serialization.Rlp.RlpException e)
        {
            return ResultWrapper<QbftBlockSealInfo?>.Fail($"Block {header.Number} does not carry BFT extra data: {e.Message}", ErrorCodes.InternalError);
        }

        return ResultWrapper<QbftBlockSealInfo?>.Success(new QbftBlockSealInfo
        {
            BlockHash = header.Hash!,
            BlockNumber = header.Number,
            Proposer = BftBlockInterface.GetProposer(header),
            Round = extraData.Round,
            Committers = header.IsGenesis ? [] : BftBlockHashing.RecoverCommitters(header, extraData, _blockInterface.CodecFor(header)),
            Validators = [.. extraData.Validators],
            VoteRecipient = extraData.Vote?.Recipient,
            VoteIsAdd = extraData.Vote?.IsAdd,
        });
    }

    public ResultWrapper<QbftConfigForRpc> qbft_getConfig(BlockParameter? blockParameter = null)
    {
        BlockHeader? header = _blockTree.FindHeader(blockParameter ?? BlockParameter.Latest);
        if (header is null)
        {
            return ResultWrapper<QbftConfigForRpc>.Fail("Block not found", ErrorCodes.ResourceNotFound);
        }

        BftConfigSnapshot config = _forksSchedule.GetFork((long)header.Number, header.Timestamp);
        return ResultWrapper<QbftConfigForRpc>.Success(new QbftConfigForRpc
        {
            EpochLength = config.EpochLength,
            BlockPeriodSeconds = config.BlockPeriodSeconds,
            EmptyBlockPeriodSeconds = config.EmptyBlockPeriodSeconds,
            XBlockPeriodMilliseconds = config.XBlockPeriodMilliseconds,
            RequestTimeoutSeconds = config.RequestTimeoutSeconds,
            ValidatorSelectionMode = config.IsValidatorContractMode ? "contract" : "blockheader",
            ValidatorContractAddress = config.ValidatorContractAddress,
            MiningBeneficiary = config.MiningBeneficiary,
            BlockReward = config.BlockReward,
            PerTxGasLimit = config.PerTxGasLimit,
        });
    }

    public ResultWrapper<QbftNodeStatus> qbft_getNodeStatus()
    {
        BlockHeader? head = _blockTree.Head?.Header;
        Address[] validators = head is null ? [] : [.. _validatorProvider.GetValidatorsAfterBlock(head)];
        ConsensusRoundIdentifier? round = _status.CurrentRound;
        return ResultWrapper<QbftNodeStatus>.Success(new QbftNodeStatus
        {
            LocalAddress = _signer.Address,
            IsValidator = validators.ContainsAddress(_signer.Address),
            CanSign = _signer.CanSign,
            ConsensusRunning = _status.IsRunning,
            ChainHeight = head?.Number ?? 0,
            Validators = validators,
            ConnectedValidators = _validatorPeers.ConnectedValidatorCount,
            CurrentSequence = round?.Sequence,
            CurrentRound = round?.Round,
            ProposerForCurrentRound = round is null ? null : _status.ProposerForRound(round.Value),
        });
    }

}

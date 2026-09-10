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
    QbftBlockInterface blockInterface,
    QbftForksSchedule forksSchedule,
    QbftChainSpecEngineParameters parameters,
    ISigner signer,
    ValidatorPeers validatorPeers,
    QbftConsensusStatus status) : IQbftRpcModule
{
    private const long DefaultRangeBlocks = 100;
    /// <summary>Besu's <c>RpcErrorType.METHOD_NOT_ENABLED</c>.</summary>
    public const int MethodNotEnabledErrorCode = -32604;

    private readonly IBlockTree _blockTree = blockTree;
    private readonly IValidatorProvider _validatorProvider = validatorProvider;
    private readonly IValidatorProvider _readOnlyValidatorProvider = new ForkingValidatorProvider(
        blockTree,
        forksSchedule,
        BlockValidatorProvider.NonForking(blockTree, epochManager, blockInterface),
        new TransactionValidatorProvider(blockTree, validatorContract, forksSchedule));
    private readonly QbftBlockInterface _blockInterface = blockInterface;
    private readonly QbftForksSchedule _forksSchedule = forksSchedule;
    private readonly QbftChainSpecEngineParameters _parameters = parameters;
    private readonly ISigner _signer = signer;
    private readonly ValidatorPeers _validatorPeers = validatorPeers;
    private readonly QbftConsensusStatus _status = status;

    public ResultWrapper<bool> qbft_discardValidatorVote(Address validatorAddress)
    {
        IVoteProvider? votes = _validatorProvider.GetVoteProviderAtHead();
        if (votes is null) return MethodNotEnabled<bool>();
        votes.DiscardVote(validatorAddress);
        return ResultWrapper<bool>.Success(true);
    }

    public ResultWrapper<IReadOnlyDictionary<Address, bool>> qbft_getPendingVotes()
    {
        IVoteProvider? votes = _validatorProvider.GetVoteProviderAtHead();
        if (votes is null) return MethodNotEnabled<IReadOnlyDictionary<Address, bool>>();
        Dictionary<Address, bool> result = [];
        foreach ((Address address, VoteType type) in votes.GetProposals())
        {
            result[address] = type == VoteType.Add;
        }

        return ResultWrapper<IReadOnlyDictionary<Address, bool>>.Success(result);
    }

    public ResultWrapper<bool> qbft_proposeValidatorVote(Address validatorAddress, bool add)
    {
        IVoteProvider? votes = _validatorProvider.GetVoteProviderAtHead();
        if (votes is null) return MethodNotEnabled<bool>();
        if (add) votes.AuthVote(validatorAddress);
        else votes.DropVote(validatorAddress);
        return ResultWrapper<bool>.Success(true);
    }

    public ResultWrapper<int> qbft_getRequestTimeoutSeconds() => ResultWrapper<int>.Success(_parameters.RequestTimeoutSeconds);

    public ResultWrapper<Address[]?> qbft_getValidatorsByBlockHash(Hash256 blockHash)
    {
        BlockHeader? header = _blockTree.FindHeader(blockHash, BlockTreeLookupOptions.None);
        return ResultWrapper<Address[]?>.Success(header is null ? null : [.. _readOnlyValidatorProvider.GetValidatorsForBlock(header)]);
    }

    public ResultWrapper<Address[]?> qbft_getValidatorsByBlockNumber(BlockParameter blockParameter)
    {
        if (blockParameter.Type == BlockParameterType.Pending)
        {
            BlockHeader? head = _blockTree.Head?.Header;
            return ResultWrapper<Address[]?>.Success(head is null ? null : [.. _readOnlyValidatorProvider.GetValidatorsAfterBlock(head)]);
        }

        BlockHeader? header = _blockTree.FindHeader(blockParameter);
        return ResultWrapper<Address[]?>.Success(header is null ? null : [.. _readOnlyValidatorProvider.GetValidatorsForBlock(header)]);
    }

    public ResultWrapper<SignerMetricResult[]> qbft_getSignerMetrics(BlockParameter? fromBlock = null, BlockParameter? toBlock = null)
    {
        long headNumber = (long)(_blockTree.Head?.Number ?? 0);
        long from = fromBlock is null ? System.Math.Max(0, headNumber - DefaultRangeBlocks) : ResolveBlockNumber(fromBlock, headNumber);
        long to = toBlock is null ? headNumber : System.Math.Min(ResolveBlockNumber(toBlock, headNumber), headNumber);
        if (from >= to)
        {
            return ResultWrapper<SignerMetricResult[]>.Fail("Invalid block range: fromBlock must be lower than toBlock", ErrorCodes.InvalidParams);
        }

        Dictionary<Address, SignerMetricResult> metrics = [];
        long last = to - 1;
        for (long number = from; number < to; number++)
        {
            BlockHeader? header = _blockTree.FindHeader((ulong)number, BlockTreeLookupOptions.RequireCanonical);
            if (header is null) continue;

            Address proposer = QbftBlockInterface.GetProposer(header);
            SignerMetricResult metric = GetOrAdd(metrics, proposer);
            metric.ProposedBlockCount += 1;
            metric.LastProposedBlockNumber = (UInt256)number;

            // Every validator of the last block shows up even when it proposed nothing in the range.
            if (number == last)
            {
                foreach (Address validator in _readOnlyValidatorProvider.GetValidatorsAfterBlock(header))
                {
                    GetOrAdd(metrics, validator);
                }
            }
        }

        return ResultWrapper<SignerMetricResult[]>.Success([.. metrics.Values]);
    }

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
            Proposer = QbftBlockInterface.GetProposer(header),
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

        QbftConfigSnapshot config = _forksSchedule.GetFork((long)header.Number, header.Timestamp);
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

    private static ResultWrapper<T> MethodNotEnabled<T>() =>
        ResultWrapper<T>.Fail("Method not enabled: validators are selected by contract on this chain", MethodNotEnabledErrorCode);

    private long ResolveBlockNumber(BlockParameter parameter, long headNumber) => parameter.Type switch
    {
        BlockParameterType.BlockNumber => (long)parameter.BlockNumber!.Value,
        BlockParameterType.Earliest => 0,
        BlockParameterType.BlockHash => (long)(_blockTree.FindHeader(parameter.BlockHash!, BlockTreeLookupOptions.None)?.Number ?? (ulong)headNumber),
        _ => headNumber,
    };

    private static SignerMetricResult GetOrAdd(Dictionary<Address, SignerMetricResult> metrics, Address address)
    {
        if (!metrics.TryGetValue(address, out SignerMetricResult? metric))
        {
            metric = new SignerMetricResult(address);
            metrics[address] = metric;
        }

        return metric;
    }
}

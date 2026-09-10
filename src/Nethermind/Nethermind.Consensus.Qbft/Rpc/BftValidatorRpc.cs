// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Consensus.Qbft.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.JsonRpc;

namespace Nethermind.Consensus.Qbft.Rpc;

/// <summary>
/// The validator and signer queries Besu exposes identically under <c>qbft_</c> and <c>ibft_</c>.
/// </summary>
/// <remarks>
/// Reads go through their own validator provider so that RPC queries on old blocks do not evict the
/// vote-tally entries consensus relies on, as in Besu.
/// </remarks>
public sealed class BftValidatorRpc(
    IBlockTree blockTree,
    IValidatorProvider validatorProvider,
    IValidatorProvider readOnlyValidatorProvider)
{
    private const long DefaultRangeBlocks = 100;

    /// <summary>Besu's <c>RpcErrorType.METHOD_NOT_ENABLED</c>.</summary>
    public const int MethodNotEnabledErrorCode = -32604;

    public ResultWrapper<bool> DiscardValidatorVote(Address validatorAddress)
    {
        IVoteProvider? votes = validatorProvider.GetVoteProviderAtHead();
        if (votes is null) return MethodNotEnabled<bool>();
        votes.DiscardVote(validatorAddress);
        return ResultWrapper<bool>.Success(true);
    }

    public ResultWrapper<IReadOnlyDictionary<Address, bool>> GetPendingVotes()
    {
        IVoteProvider? votes = validatorProvider.GetVoteProviderAtHead();
        if (votes is null) return MethodNotEnabled<IReadOnlyDictionary<Address, bool>>();
        Dictionary<Address, bool> result = [];
        foreach ((Address address, VoteType type) in votes.GetProposals())
        {
            result[address] = type == VoteType.Add;
        }

        return ResultWrapper<IReadOnlyDictionary<Address, bool>>.Success(result);
    }

    public ResultWrapper<bool> ProposeValidatorVote(Address validatorAddress, bool add)
    {
        IVoteProvider? votes = validatorProvider.GetVoteProviderAtHead();
        if (votes is null) return MethodNotEnabled<bool>();
        if (add) votes.AuthVote(validatorAddress);
        else votes.DropVote(validatorAddress);
        return ResultWrapper<bool>.Success(true);
    }

    public ResultWrapper<Address[]?> GetValidatorsByBlockHash(Hash256 blockHash)
    {
        BlockHeader? header = blockTree.FindHeader(blockHash, BlockTreeLookupOptions.None);
        return ResultWrapper<Address[]?>.Success(header is null ? null : [.. readOnlyValidatorProvider.GetValidatorsForBlock(header)]);
    }

    public ResultWrapper<Address[]?> GetValidatorsByBlockNumber(BlockParameter blockParameter)
    {
        if (blockParameter.Type == BlockParameterType.Pending)
        {
            BlockHeader? head = blockTree.Head?.Header;
            return ResultWrapper<Address[]?>.Success(head is null ? null : [.. readOnlyValidatorProvider.GetValidatorsAfterBlock(head)]);
        }

        BlockHeader? header = blockTree.FindHeader(blockParameter);
        return ResultWrapper<Address[]?>.Success(header is null ? null : [.. readOnlyValidatorProvider.GetValidatorsForBlock(header)]);
    }

    public ResultWrapper<SignerMetricResult[]> GetSignerMetrics(BlockParameter? fromBlock = null, BlockParameter? toBlock = null)
    {
        long headNumber = (long)(blockTree.Head?.Number ?? 0);
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
            BlockHeader? header = blockTree.FindHeader((ulong)number, BlockTreeLookupOptions.RequireCanonical);
            if (header is null) continue;

            Address proposer = BftBlockInterface.GetProposer(header);
            SignerMetricResult metric = GetOrAdd(metrics, proposer);
            metric.ProposedBlockCount += 1;
            metric.LastProposedBlockNumber = (UInt256)number;

            // Every validator of the last block shows up even when it proposed nothing in the range.
            if (number == last)
            {
                foreach (Address validator in readOnlyValidatorProvider.GetValidatorsAfterBlock(header))
                {
                    GetOrAdd(metrics, validator);
                }
            }
        }

        return ResultWrapper<SignerMetricResult[]>.Success([.. metrics.Values]);
    }

    public long ResolveBlockNumber(BlockParameter parameter, long headNumber) => parameter.Type switch
    {
        BlockParameterType.BlockNumber => (long)parameter.BlockNumber!.Value,
        BlockParameterType.Earliest => 0,
        BlockParameterType.BlockHash => (long)(blockTree.FindHeader(parameter.BlockHash!, BlockTreeLookupOptions.None)?.Number ?? (ulong)headNumber),
        _ => headNumber,
    };

    private static ResultWrapper<T> MethodNotEnabled<T>() =>
        ResultWrapper<T>.Fail("Method not enabled: validators are selected by contract on this chain", MethodNotEnabledErrorCode);

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

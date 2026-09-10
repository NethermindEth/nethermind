// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Consensus.Qbft.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.Consensus.Qbft.Rpc;

public static class IbftRpcModuleType
{
    public const string Ibft = "Ibft";
}

/// <summary>Besu's IBFT 2.0 JSON-RPC group.</summary>
[RpcModule(IbftRpcModuleType.Ibft)]
public interface IIbftRpcModule : IRpcModule
{
    [JsonRpcMethod(Description = "Discards a pending vote for the given validator.", IsImplemented = true)]
    ResultWrapper<bool> ibft_discardValidatorVote(Address validatorAddress);

    [JsonRpcMethod(Description = "Returns the votes this node is casting, keyed by validator; true adds, false drops.", IsImplemented = true)]
    ResultWrapper<IReadOnlyDictionary<Address, bool>> ibft_getPendingVotes();

    [JsonRpcMethod(Description = "Returns how many blocks each validator proposed in a block range, and the last one it proposed.", IsImplemented = true)]
    ResultWrapper<SignerMetricResult[]> ibft_getSignerMetrics(BlockParameter? fromBlock = null, BlockParameter? toBlock = null);

    [JsonRpcMethod(Description = "Returns the validators recorded for the block with the given hash.", IsImplemented = true)]
    ResultWrapper<Address[]?> ibft_getValidatorsByBlockHash(Hash256 blockHash);

    [JsonRpcMethod(Description = "Returns the validators recorded for the given block; pending returns those for the block being built.", IsImplemented = true)]
    ResultWrapper<Address[]?> ibft_getValidatorsByBlockNumber(BlockParameter blockParameter);

    [JsonRpcMethod(Description = "Casts a vote to add or drop a validator.", IsImplemented = true)]
    ResultWrapper<bool> ibft_proposeValidatorVote(Address validatorAddress, bool add);
}

/// <summary>Besu's six <c>ibft_</c> methods over the implementation shared with <c>qbft_</c>.</summary>
public sealed class Ibft2RpcModule(
    IBlockTree blockTree,
    IValidatorProvider validatorProvider,
    EpochManager epochManager,
    BftBlockInterface blockInterface) : IIbftRpcModule
{
    // IBFT 2.0 has no validator contract mode, so reads only need their own vote-tally cache, kept
    // separate from the consensus one so that queries on old blocks do not evict its entries.
    private readonly BftValidatorRpc _bft = new(
        blockTree,
        validatorProvider,
        BlockValidatorProvider.NonForking(blockTree, epochManager, blockInterface));

    public ResultWrapper<bool> ibft_discardValidatorVote(Address validatorAddress) => _bft.DiscardValidatorVote(validatorAddress);

    public ResultWrapper<IReadOnlyDictionary<Address, bool>> ibft_getPendingVotes() => _bft.GetPendingVotes();

    public ResultWrapper<SignerMetricResult[]> ibft_getSignerMetrics(BlockParameter? fromBlock = null, BlockParameter? toBlock = null) =>
        _bft.GetSignerMetrics(fromBlock, toBlock);

    public ResultWrapper<Address[]?> ibft_getValidatorsByBlockHash(Hash256 blockHash) => _bft.GetValidatorsByBlockHash(blockHash);

    public ResultWrapper<Address[]?> ibft_getValidatorsByBlockNumber(BlockParameter blockParameter) => _bft.GetValidatorsByBlockNumber(blockParameter);

    public ResultWrapper<bool> ibft_proposeValidatorVote(Address validatorAddress, bool add) => _bft.ProposeValidatorVote(validatorAddress, add);
}

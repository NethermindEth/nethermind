// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.Consensus.Qbft.Rpc;

public static class QbftRpcModuleType
{
    public const string Qbft = "Qbft";
}

/// <summary>One validator's share of a block range, as returned by <c>qbft_getSignerMetrics</c>.</summary>
public sealed class SignerMetricResult(Address address)
{
    public Address Address { get; } = address;
    public UInt256 ProposedBlockCount { get; set; }
    public UInt256 LastProposedBlockNumber { get; set; }
}

/// <summary>Consensus fields of one block, as returned by <c>qbft_getBlockCommitters</c>.</summary>
public sealed class QbftBlockSealInfo
{
    public required Hash256 BlockHash { get; init; }
    public required UInt256 BlockNumber { get; init; }
    public required Address Proposer { get; init; }
    public required int Round { get; init; }
    public required Address[] Committers { get; init; }
    public required Address[] Validators { get; init; }
    public Address? VoteRecipient { get; init; }
    public bool? VoteIsAdd { get; init; }
}

/// <summary>The QBFT options in force at a block, as returned by <c>qbft_getConfig</c>.</summary>
public sealed class QbftConfigForRpc
{
    public required long EpochLength { get; init; }
    public required int BlockPeriodSeconds { get; init; }
    public required int EmptyBlockPeriodSeconds { get; init; }
    public required long XBlockPeriodMilliseconds { get; init; }
    public required int RequestTimeoutSeconds { get; init; }
    public required string ValidatorSelectionMode { get; init; }
    public Address? ValidatorContractAddress { get; init; }
    public Address? MiningBeneficiary { get; init; }
    public required UInt256 BlockReward { get; init; }
    public ulong? PerTxGasLimit { get; init; }
}

/// <summary>The local node's view of consensus, as returned by <c>qbft_getNodeStatus</c>.</summary>
public sealed class QbftNodeStatus
{
    public required Address LocalAddress { get; init; }
    public required bool IsValidator { get; init; }
    public required bool CanSign { get; init; }
    public required bool ConsensusRunning { get; init; }
    public required UInt256 ChainHeight { get; init; }
    public required Address[] Validators { get; init; }
    public required int ConnectedValidators { get; init; }
    public long? CurrentSequence { get; init; }
    public int? CurrentRound { get; init; }
    public Address? ProposerForCurrentRound { get; init; }
}

[RpcModule(QbftRpcModuleType.Qbft)]
public interface IQbftRpcModule : IRpcModule
{
    [JsonRpcMethod(Description = "Discards a pending proposal to add or remove the validator. Not available in validator contract mode.", IsImplemented = true)]
    ResultWrapper<bool> qbft_discardValidatorVote(Address validatorAddress);

    [JsonRpcMethod(Description = "Returns the pending validator membership proposals of this node: address to true (add) or false (drop). Not available in validator contract mode.", IsImplemented = true)]
    ResultWrapper<IReadOnlyDictionary<Address, bool>> qbft_getPendingVotes();

    [JsonRpcMethod(Description = "Returns, per validator, how many blocks it proposed in [fromBlock, toBlock) and the last one; defaults to the last 100 blocks.", IsImplemented = true)]
    ResultWrapper<SignerMetricResult[]> qbft_getSignerMetrics(BlockParameter? fromBlock = null, BlockParameter? toBlock = null);

    [JsonRpcMethod(Description = "Returns the validators recorded for the block with the given hash, or null when the block is unknown.", IsImplemented = true)]
    ResultWrapper<Address[]?> qbft_getValidatorsByBlockHash(Hash256 blockHash);

    [JsonRpcMethod(Description = "Returns the validators recorded for the block; 'pending' returns the validators for the next block.", IsImplemented = true)]
    ResultWrapper<Address[]?> qbft_getValidatorsByBlockNumber(BlockParameter blockParameter);

    [JsonRpcMethod(Description = "Proposes adding (true) or removing (false) a validator; the vote is cast in the blocks this node proposes. Not available in validator contract mode.", IsImplemented = true)]
    ResultWrapper<bool> qbft_proposeValidatorVote(Address validatorAddress, bool add);

    [JsonRpcMethod(Description = "Returns the base round timeout in seconds.", IsImplemented = true)]
    ResultWrapper<int> qbft_getRequestTimeoutSeconds();

    [JsonRpcMethod(Description = "Returns the proposer, round, recovered committers, validators and vote of a block. Nethermind extension.", IsImplemented = true)]
    ResultWrapper<QbftBlockSealInfo?> qbft_getBlockCommitters(BlockParameter blockParameter);

    [JsonRpcMethod(Description = "Returns the QBFT options in force at the block (default: latest), after applying transitions. Nethermind extension.", IsImplemented = true)]
    ResultWrapper<QbftConfigForRpc> qbft_getConfig(BlockParameter? blockParameter = null);

    [JsonRpcMethod(Description = "Returns this node's consensus status: address, validator membership, current sequence and round. Nethermind extension.", IsImplemented = true)]
    ResultWrapper<QbftNodeStatus> qbft_getNodeStatus();
}

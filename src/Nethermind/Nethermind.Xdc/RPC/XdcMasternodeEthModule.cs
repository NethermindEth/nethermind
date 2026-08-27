// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Xdc.Contracts;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;

namespace Nethermind.Xdc.RPC;

internal sealed class XdcMasternodeEthModule(
    IBlockTree tree,
    ISpecProvider specProvider,
    IEpochSwitchManager epochSwitchManager,
    ISigningTxCache signingTxCache,
    IMasternodeVotingContract masternodeVotingContract,
    IMintedRecordContract mintedRecordContract,
    IRewardsStore rewardsStore,
    IReadOnlyTxProcessingEnvFactory readOnlyTxProcessingEnvFactory) : IXdcMasternodeEthRpcModule
{
    /// <summary>Reported instead of a stake when the address is a masternode that is no longer a candidate.</summary>
    private static readonly BigInteger UnknownCapacity = BigInteger.MinusOne;

    private readonly EthereumEcdsa _ethereumEcdsa = new(specProvider.ChainId);

    public ResultWrapper<Address[]> eth_getBlockSignersByHash(Hash256 blockHash)
    {
        BlockHeader? header = tree.FindHeader(blockHash);
        return header is null
            ? ResultWrapper<Address[]>.Success([])
            : GetBlockSigners(header);
    }

    public ResultWrapper<Address[]> eth_getBlockSignersByNumber(BlockParameter? blockParameter = null)
    {
        SearchResult<BlockHeader> search = tree.SearchForHeader(blockParameter ?? BlockParameter.Latest);
        return search.IsError
            ? ResultWrapper<Address[]>.Fail(search)
            : GetBlockSigners(search.Object!);
    }

    public ResultWrapper<uint> eth_getBlockFinalityByHash(Hash256 blockHash)
    {
        BlockHeader? header = tree.FindHeader(blockHash);
        return header is null
            ? ResultWrapper<uint>.Success(0)
            : GetBlockFinality(header);
    }

    public ResultWrapper<uint> eth_getBlockFinalityByNumber(BlockParameter? blockParameter = null)
    {
        SearchResult<BlockHeader> search = tree.SearchForHeader(blockParameter ?? BlockParameter.Latest);
        return search.IsError
            ? ResultWrapper<uint>.Fail(search)
            : GetBlockFinality(search.Object!);
    }

    public ResultWrapper<XdcCandidatesResult> eth_getCandidates(XdcEpochParameter? epoch = null)
    {
        if (!TryResolveCheckpoint(epoch, out EpochCheckpoint resolved, out string? error))
        {
            return ResultWrapper<XdcCandidatesResult>.Fail(error);
        }

        XdcCandidatesResult result = new() { Epoch = (long)resolved.EpochNumber };

        CandidateStake[] candidates = GetCandidateStakes(resolved.CandidateStateHeader);
        Address[] masternodes = GetCheckpointMasternodes(resolved.Header);
        if (candidates.Length == 0 || masternodes.Length == 0)
        {
            return ResultWrapper<XdcCandidatesResult>.Success(result);
        }

        Dictionary<string, XdcCandidateInfo> statuses = new(candidates.Length);
        foreach (CandidateStake candidate in candidates)
        {
            statuses[candidate.Address.ToString()] = new XdcCandidateInfo
            {
                Status = XdcConstants.RpcCandidateStatusProposed,
                Capacity = ToCapacity(candidate.Stake),
            };
        }

        foreach (Address masternode in masternodes)
        {
            string key = masternode.ToString();
            if (statuses.TryGetValue(key, out XdcCandidateInfo? candidateInfo))
            {
                candidateInfo.Status = XdcConstants.RpcCandidateStatusMasternode;
            }
            else
            {
                statuses[key] = new XdcCandidateInfo
                {
                    Status = XdcConstants.RpcCandidateStatusMasternode,
                    Capacity = UnknownCapacity,
                };
            }
        }

        result.Success = true;
        result.Candidates = statuses;

        int maxMasternodes = resolved.Spec.MaxMasternodes;
        if (masternodes.Length >= maxMasternodes)
        {
            return ResultWrapper<XdcCandidatesResult>.Success(result);
        }

        if (candidates.Length > maxMasternodes)
        {
            SortByStakeDescending(candidates);
        }

        Address[] penalties = CollectPenalties(resolved.Header, resolved.EpochNumber);
        int slots = masternodes.Length;
        foreach (CandidateStake candidate in candidates)
        {
            foreach (Address penalty in penalties)
            {
                if (candidate.Address != penalty)
                {
                    continue;
                }

                statuses[penalty.ToString()].Status = XdcConstants.RpcCandidateStatusSlashed;
                if (++slots >= maxMasternodes)
                {
                    return ResultWrapper<XdcCandidatesResult>.Success(result);
                }
            }
        }

        return ResultWrapper<XdcCandidatesResult>.Success(result);
    }

    public ResultWrapper<XdcCandidateStatusResult> eth_getCandidateStatus(Address coinbase, XdcEpochParameter? epoch = null)
    {
        if (!TryResolveCheckpoint(epoch, out EpochCheckpoint resolved, out string? error))
        {
            return ResultWrapper<XdcCandidateStatusResult>.Fail(error);
        }

        XdcCandidateStatusResult result = new() { Epoch = (long)resolved.EpochNumber };

        CandidateStake[] candidates = GetCandidateStakes(resolved.CandidateStateHeader);
        Address[] masternodes = GetCheckpointMasternodes(resolved.Header);
        if (candidates.Length == 0 || masternodes.Length == 0)
        {
            return ResultWrapper<XdcCandidateStatusResult>.Success(result);
        }

        result.Success = true;

        bool isCandidate = false;
        foreach (CandidateStake candidate in candidates)
        {
            if (candidate.Address != coinbase)
            {
                continue;
            }

            isCandidate = true;
            result.Status = XdcConstants.RpcCandidateStatusProposed;
            result.Capacity = ToCapacity(candidate.Stake);
            break;
        }

        foreach (Address masternode in masternodes)
        {
            if (masternode != coinbase)
            {
                continue;
            }

            result.Status = XdcConstants.RpcCandidateStatusMasternode;
            if (!isCandidate)
            {
                result.Capacity = UnknownCapacity;
            }

            return ResultWrapper<XdcCandidateStatusResult>.Success(result);
        }

        int maxMasternodes = resolved.Spec.MaxMasternodes;
        if (!isCandidate || masternodes.Length >= maxMasternodes)
        {
            return ResultWrapper<XdcCandidateStatusResult>.Success(result);
        }

        if (candidates.Length > maxMasternodes)
        {
            SortByStakeDescending(candidates);
        }

        Address[] penalties = CollectPenalties(resolved.Header, resolved.EpochNumber);
        int slots = masternodes.Length;
        foreach (CandidateStake candidate in candidates)
        {
            foreach (Address penalty in penalties)
            {
                if (candidate.Address != penalty)
                {
                    continue;
                }

                if (penalty == coinbase)
                {
                    result.Status = XdcConstants.RpcCandidateStatusSlashed;
                    return ResultWrapper<XdcCandidateStatusResult>.Success(result);
                }

                if (++slots >= maxMasternodes)
                {
                    return ResultWrapper<XdcCandidateStatusResult>.Success(result);
                }
            }
        }

        return ResultWrapper<XdcCandidateStatusResult>.Success(result);
    }

    public ResultWrapper<double> eth_getStakerROI()
    {
        if (!TryGetHead(out ChainHead head, out string? error))
        {
            return ResultWrapper<double>.Fail(error);
        }

        // Rewards for an epoch are only paid out at the next checkpoint, so the last settled one is an epoch back.
        ulong currentEpoch = head.EpochNumber;
        if (currentEpoch < 1 || !TryFindCheckpointHeader(currentEpoch - 1, out XdcBlockHeader? rewardedCheckpoint))
        {
            return ResultWrapper<double>.Success(0);
        }

        UInt256 totalCap = UInt256.Zero;
        foreach (CandidateStake candidate in GetCandidateStakes(rewardedCheckpoint))
        {
            totalCap += candidate.Stake;
        }

        UInt256 masternodeReward = (UInt256)head.Spec.Reward * Unit.Ether;
        return ResultWrapper<double>.Success(CalculateRoi(masternodeReward, totalCap, currentEpoch));
    }

    public ResultWrapper<double> eth_getStakerROIMasternode(Address masternode)
    {
        if (!TryGetHead(out ChainHead head, out string? error))
        {
            return ResultWrapper<double>.Fail(error);
        }

        ulong currentEpoch = head.EpochNumber;
        if (currentEpoch < 1
            || !TryFindCheckpointHeader(currentEpoch, out XdcBlockHeader? currentCheckpoint)
            || !TryFindCheckpointHeader(currentEpoch - 1, out XdcBlockHeader? rewardedCheckpoint)
            || rewardedCheckpoint.Hash is null)
        {
            return ResultWrapper<double>.Success(0);
        }

        if (!rewardsStore.TryGetEpochRewards(rewardedCheckpoint.Hash, out XdcEpochRewards? epochRewards)
            || epochRewards is null
            || !epochRewards.Rewards.TryGetValue(masternode.ToString(), out Dictionary<string, string>? holderRewards))
        {
            return ResultWrapper<double>.Success(0);
        }

        UInt256 masternodeReward = UInt256.Zero;
        foreach (string holderReward in holderRewards.Values)
        {
            if (UInt256.TryParse(holderReward, out UInt256 reward))
            {
                masternodeReward += reward;
            }
        }

        return ResultWrapper<double>.Success(
            CalculateRoi(masternodeReward, GetTotalVoterStake(currentCheckpoint, masternode), currentEpoch));
    }

    public ResultWrapper<XdcTokenSupply> eth_getTokenStats(XdcEpochParameter? epoch = null)
    {
        if (!TryGetHead(out ChainHead head, out string? error))
        {
            return ResultWrapper<XdcTokenSupply>.Fail(error);
        }

        using IReadOnlyTxProcessorSource source = readOnlyTxProcessingEnvFactory.Create();
        using IReadOnlyTxProcessingScope scope = source.Build(head.Header);
        IWorldState worldState = scope.WorldState;

        if (!mintedRecordContract.TryGetOnsetEpoch(worldState, out UInt256 onsetEpoch))
        {
            return ResultWrapper<XdcTokenSupply>.Fail("Minted record is not initialized because the reward upgrade has not been applied");
        }

        ulong epochNumber = head.EpochNumber;
        if (epoch?.EpochNumber is ulong requestedEpoch)
        {
            if (requestedEpoch < onsetEpoch)
            {
                return ResultWrapper<XdcTokenSupply>.Fail("Epoch number is before the reward upgrade");
            }

            if (requestedEpoch > head.EpochNumber)
            {
                return ResultWrapper<XdcTokenSupply>.Fail("Epoch number is after the current epoch");
            }

            epochNumber = requestedEpoch;
        }

        MintedRecordAccounting accounting = mintedRecordContract.GetEpochAccounting(worldState, epochNumber);

        // Every epoch before the upgrade minted a flat per-epoch reward, and the onset epoch is accounted for by the upgrade.
        UInt256 preUpgradeEpochs = onsetEpoch.IsZero ? UInt256.Zero : onsetEpoch - UInt256.One;
        UInt256 preUpgradeMinted = (UInt256)head.Spec.Reward * Unit.Ether * preUpgradeEpochs;

        return ResultWrapper<XdcTokenSupply>.Success(new XdcTokenSupply
        {
            V1 = new XdcSupplyV1 { Minted = preUpgradeMinted },
            V2 = new XdcSupplyV2 { Minted = accounting.Minted, Burned = accounting.Burned },
            Minted = preUpgradeMinted + accounting.Minted,
            UpgradeEpochNum = onsetEpoch,
            EpochNum = epochNumber,
            BlockHash = accounting.RewardBlockNumber <= ulong.MaxValue
                ? tree.FindHeader(accounting.RewardBlockNumber.u0)?.Hash
                : null,
            BlockNumber = accounting.RewardBlockNumber,
        });
    }

    private ResultWrapper<Address[]> GetBlockSigners(BlockHeader header)
    {
        if (header is not XdcBlockHeader xdcHeader)
        {
            return ResultWrapper<Address[]>.Fail("Header is not an XDC block header");
        }

        IXdcReleaseSpec spec = specProvider.GetXdcSpec(xdcHeader);
        XdcBlockHeader? signedHeader = FindNearestSignedHeader(xdcHeader, spec);
        if (signedHeader is null)
        {
            return ResultWrapper<Address[]>.Success([]);
        }

        Address[] masternodes = epochSwitchManager.GetEpochSwitchInfo(signedHeader)?.Masternodes ?? [];
        return ResultWrapper<Address[]>.Success(
            masternodes.Length == 0 ? [] : CollectSigners(signedHeader, masternodes, spec));
    }

    private ResultWrapper<uint> GetBlockFinality(BlockHeader header)
    {
        if (header is not XdcBlockHeader xdcHeader)
        {
            return ResultWrapper<uint>.Fail("Header is not an XDC block header");
        }

        IXdcReleaseSpec spec = specProvider.GetXdcSpec(xdcHeader);
        XdcBlockHeader? signedHeader = FindNearestSignedHeader(xdcHeader, spec);
        if (signedHeader is null)
        {
            return ResultWrapper<uint>.Success(0);
        }

        Address[] masternodes = epochSwitchManager.GetEpochSwitchInfo(signedHeader)?.Masternodes ?? [];

        // Signatures only finalize the canonical chain; a block on a discarded branch never gains finality.
        if (masternodes.Length == 0 || header.Hash is null || !tree.IsMainChain(header.Hash, throwOnMissingHash: false))
        {
            return ResultWrapper<uint>.Success(0);
        }

        Address[] signers = CollectSigners(signedHeader, masternodes, spec);
        return ResultWrapper<uint>.Success((uint)(100 * signers.Length / masternodes.Length));
    }

    /// <summary>
    /// Resolves the block whose sign transactions represent the given block.
    /// </summary>
    /// <remarks>
    /// Masternodes only sign blocks at heights that are multiples of <see cref="IXdcReleaseSpec.MergeSignRange"/>,
    /// so a block is represented by the next such height. Blocks too close to the head fall back to themselves,
    /// because the representative block does not exist yet.
    /// </remarks>
    private XdcBlockHeader? FindNearestSignedHeader(XdcBlockHeader header, IXdcReleaseSpec spec)
    {
        // A chainspec without a merge-sign range defines no signing schedule to resolve a representative block from.
        if (header.Number == 0 || spec.MergeSignRange == 0)
        {
            return null;
        }

        ulong headNumber = tree.Head?.Number ?? 0;
        ulong signedNumber = header.Number + (spec.MergeSignRange - header.Number % spec.MergeSignRange);
        if (signedNumber >= headNumber)
        {
            signedNumber = header.Number;
        }

        return tree.FindHeader(signedNumber) as XdcBlockHeader;
    }

    private Address[] CollectSigners(XdcBlockHeader signedHeader, Address[] masternodes, IXdcReleaseSpec spec)
    {
        Hash256? signedHash = signedHeader.Hash;
        if (signedHash is null)
        {
            return [];
        }

        HashSet<Address> pending = [.. masternodes];
        List<Address> signers = new(masternodes.Length);
        ulong headNumber = tree.Head?.Number ?? 0;
        ulong limit = Math.Min(signedHeader.Number + XdcConstants.LimitTimeFinality, headNumber);

        for (ulong number = signedHeader.Number + 1; number <= limit && pending.Count > 0; number++)
        {
            Block? block = tree.FindBlock(number, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
            if (block?.Hash is null)
            {
                break;
            }

            foreach (Transaction signingTx in signingTxCache.GetSigningTransactions(block.Hash, number, spec))
            {
                ReadOnlySpan<byte> callData = signingTx.Data.Span;
                if (callData.Length != XdcConstants.SignTransactionDataLength
                    || new Hash256(callData[^Hash256.Size..]) != signedHash)
                {
                    continue;
                }

                signingTx.SenderAddress ??= _ethereumEcdsa.RecoverAddress(signingTx);
                if (signingTx.SenderAddress is not null && pending.Remove(signingTx.SenderAddress))
                {
                    signers.Add(signingTx.SenderAddress);
                }
            }
        }

        return [.. signers];
    }

    private readonly record struct ChainHead(XdcBlockHeader Header, IXdcReleaseSpec Spec, ulong EpochNumber);

    /// <param name="CandidateStateHeader">
    /// Block whose state the candidate list is read from. For <c>latest</c> this is the head rather than the
    /// checkpoint, so candidates that proposed or resigned mid-epoch are visible, as in the reference client.
    /// </param>
    private readonly record struct EpochCheckpoint(
        XdcBlockHeader Header,
        BlockHeader CandidateStateHeader,
        ulong EpochNumber,
        IXdcReleaseSpec Spec);

    private bool TryGetHead(out ChainHead head, [NotNullWhen(false)] out string? error)
    {
        head = default;

        if (tree.Head?.Header is not XdcBlockHeader headHeader)
        {
            error = "Cannot get current block header";
            return false;
        }

        if (headHeader.ExtraConsensusData is null)
        {
            error = $"Block {headHeader.Number} does not contain consensus data (round information)";
            return false;
        }

        IXdcReleaseSpec spec = specProvider.GetXdcSpec(headHeader);
        head = new ChainHead(headHeader, spec, spec.SwitchEpoch + headHeader.ExtraConsensusData.BlockRound / spec.EpochLength);
        error = null;
        return true;
    }

    private bool TryFindCheckpointHeader(ulong epochNumber, [NotNullWhen(true)] out XdcBlockHeader? checkpoint)
    {
        BlockRoundInfo? epochBlock = epochSwitchManager.GetBlockByEpochNumber(epochNumber);
        checkpoint = epochBlock is null ? null : tree.FindHeader(epochBlock.Hash, epochBlock.BlockNumber) as XdcBlockHeader;
        return checkpoint is not null;
    }

    private bool TryResolveCheckpoint(XdcEpochParameter? epoch, out EpochCheckpoint resolved, [NotNullWhen(false)] out string? error)
    {
        resolved = default;

        if (!TryGetHead(out ChainHead head, out error))
        {
            return false;
        }

        ulong epochNumber = epoch?.EpochNumber ?? head.EpochNumber;
        if (epochNumber < head.Spec.SwitchEpoch)
        {
            error = "V1 epoch is not supported";
            return false;
        }

        if (!TryFindCheckpointHeader(epochNumber, out XdcBlockHeader? checkpoint))
        {
            error = $"Cannot find epoch {epochNumber}";
            return false;
        }

        // The masternode cap is a per-round V2 config value, so it must be read at the checkpoint being reported.
        resolved = new EpochCheckpoint(
            checkpoint,
            epoch?.EpochNumber is null ? head.Header : checkpoint,
            epochNumber,
            specProvider.GetXdcSpec(checkpoint));
        return true;
    }

    private static Address[] GetCheckpointMasternodes(XdcBlockHeader checkpoint) =>
        checkpoint.ValidatorsAddress is { } validators ? [.. validators] : [];

    /// <remarks>
    /// Reads every candidate through a single processing scope. The scopeless contract overloads build one
    /// read-only environment per call, which on a candidate list in the hundreds turns a sharable RPC method
    /// into an easy way to burn CPU.
    /// </remarks>
    private CandidateStake[] GetCandidateStakes(BlockHeader stateHeader)
    {
        using IReadOnlyTxProcessorSource source = readOnlyTxProcessingEnvFactory.Create();
        using IReadOnlyTxProcessingScope scope = source.Build(stateHeader);
        ITransactionProcessor processor = scope.TransactionProcessor;

        Address[] candidates = masternodeVotingContract.GetCandidates(processor, stateHeader) ?? [];
        List<CandidateStake> stakes = new(candidates.Length);
        foreach (Address candidate in candidates)
        {
            if (candidate == Address.Zero)
            {
                continue;
            }

            stakes.Add(new CandidateStake
            {
                Address = candidate,
                Stake = masternodeVotingContract.GetCandidateStake(processor, stateHeader, candidate),
            });
        }

        return [.. stakes];
    }

    /// <remarks>
    /// Voters are read from contract storage rather than through the EVM: the list is unbounded, and this is a
    /// sharable method, so a call per voter would let one request amplify into arbitrarily many.
    /// </remarks>
    private UInt256 GetTotalVoterStake(BlockHeader stateHeader, Address masternode)
    {
        using IReadOnlyTxProcessorSource source = readOnlyTxProcessingEnvFactory.Create();
        using IReadOnlyTxProcessingScope scope = source.Build(stateHeader);
        IWorldState worldState = scope.WorldState;

        UInt256 totalStake = UInt256.Zero;
        foreach (Address voter in masternodeVotingContract.GetVoters(worldState, masternode))
        {
            totalStake += masternodeVotingContract.GetVoterStake(worldState, masternode, voter);
        }

        return totalStake;
    }

    private static BigInteger ToCapacity(in UInt256 stake) => new(stake.ToBigEndian(), isUnsigned: true, isBigEndian: true);

    private static void SortByStakeDescending(CandidateStake[] candidates) =>
        XdcSort.Slice(candidates, static (x, y) => x.Stake > y.Stake);

    /// <summary>
    /// Collects the penalized addresses recorded on the epoch's checkpoint and the preceding
    /// <see cref="XdcConstants.PenaltyEpochLookback"/> checkpoints.
    /// </summary>
    /// <remarks>
    /// Addresses penalized in more than one epoch are reported once per epoch, matching the reference client:
    /// each repeat consumes another masternode slot when candidate status is resolved.
    /// </remarks>
    private Address[] CollectPenalties(XdcBlockHeader checkpoint, ulong epochNumber)
    {
        List<Address> penalties = [];
        if (checkpoint.PenaltiesAddress is { } checkpointPenalties)
        {
            penalties.AddRange(checkpointPenalties);
        }

        for (ulong lookback = 1; lookback <= XdcConstants.PenaltyEpochLookback && lookback <= epochNumber; lookback++)
        {
            if (TryFindCheckpointHeader(epochNumber - lookback, out XdcBlockHeader? previous)
                && previous.PenaltiesAddress is { } previousPenalties)
            {
                penalties.AddRange(previousPenalties);
            }
        }

        return [.. penalties];
    }

    /// <summary>
    /// Projects one epoch of rewards over a year and expresses it as a percentage of the staked total.
    /// </summary>
    /// <remarks>
    /// Half of a masternode's reward reaches its stakers, so only that half is annualized. Returns zero when
    /// the inputs cannot produce a meaningful rate — no stake, no reward, or an unmeasurable epoch duration.
    /// </remarks>
    private double CalculateRoi(UInt256 reward, UInt256 totalCap, ulong currentEpoch)
    {
        ulong epochDuration = GetEpochDuration(currentEpoch);
        if (epochDuration == 0 || totalCap.IsZero)
        {
            return 0;
        }

        UInt256 stakerRewardPerYear = reward / 2 * (XdcConstants.SecondsPerYear / epochDuration);
        if (stakerRewardPerYear.IsZero)
        {
            return 0;
        }

        UInt256 capPerReward = totalCap / stakerRewardPerYear;
        return capPerReward.IsZero || capPerReward > ulong.MaxValue ? 0 : 100.0 / capPerReward.u0;
    }

    /// <summary>Measures how long an epoch takes, in seconds, from the two most recent checkpoints.</summary>
    private ulong GetEpochDuration(ulong currentEpoch)
    {
        if (currentEpoch < 1
            || !TryFindCheckpointHeader(currentEpoch, out XdcBlockHeader? current)
            || !TryFindCheckpointHeader(currentEpoch - 1, out XdcBlockHeader? previous)
            || current.Timestamp <= previous.Timestamp)
        {
            return 0;
        }

        return current.Timestamp - previous.Timestamp;
    }
}

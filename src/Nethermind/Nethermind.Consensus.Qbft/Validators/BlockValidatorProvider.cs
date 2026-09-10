// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Consensus.Qbft.Config;
using Nethermind.Core;

namespace Nethermind.Consensus.Qbft.Validators;

/// <summary>Validators tallied from block-header votes ("blockheader" selection mode).</summary>
public sealed class BlockValidatorProvider : IValidatorProvider
{
    private readonly VoteTallyCache _voteTallyCache;
    private readonly QbftBlockInterface _blockInterface;
    private readonly BlockVoteProvider _voteProvider;

    private BlockValidatorProvider(VoteTallyCache voteTallyCache, QbftBlockInterface blockInterface)
    {
        _voteTallyCache = voteTallyCache;
        _blockInterface = blockInterface;
        _voteProvider = new BlockVoteProvider(voteTallyCache, new VoteProposer());
    }

    /// <summary>Provider honouring the validator lists installed by <c>transitions.qbft</c>.</summary>
    public static BlockValidatorProvider Forking(IBlockTree blockTree, EpochManager epochManager, QbftBlockInterface blockInterface, QbftForksSchedule forksSchedule) =>
        new(new ForkingVoteTallyCache(blockTree, new VoteTallyUpdater(epochManager, blockInterface), epochManager, blockInterface, forksSchedule), blockInterface);

    /// <summary>Provider with its own tally cache, for read-only consumers such as RPC that must not share the consensus vote store.</summary>
    public static BlockValidatorProvider NonForking(IBlockTree blockTree, EpochManager epochManager, QbftBlockInterface blockInterface) =>
        new(new VoteTallyCache(blockTree, new VoteTallyUpdater(epochManager, blockInterface), epochManager, blockInterface), blockInterface);

    public IReadOnlyList<Address> GetValidatorsAtHead() => _voteTallyCache.GetVoteTallyAtHead().Validators;

    public IReadOnlyList<Address> GetValidatorsAfterBlock(BlockHeader parentHeader) => _voteTallyCache.GetVoteTallyAfterBlock(parentHeader).Validators;

    public IReadOnlyList<Address> GetValidatorsForBlock(BlockHeader header)
    {
        Address[] validators = [.. _blockInterface.ValidatorsInBlock(header)];
        Array.Sort(validators);
        return validators;
    }

    public IVoteProvider? GetVoteProviderAtHead() => _voteProvider;

    private sealed class BlockVoteProvider(VoteTallyCache voteTallyCache, VoteProposer voteProposer) : IVoteProvider
    {
        public ValidatorVote? GetVoteAfterBlock(BlockHeader header, Address localAddress) =>
            voteProposer.GetVote(localAddress, voteTallyCache.GetVoteTallyAfterBlock(header));

        public void AuthVote(Address address) => voteProposer.Auth(address);
        public void DropVote(Address address) => voteProposer.Drop(address);
        public void DiscardVote(Address address) => voteProposer.Discard(address);
        public IReadOnlyDictionary<Address, VoteType> GetProposals() => voteProposer.GetProposals();
    }
}

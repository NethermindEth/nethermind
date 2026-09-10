// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Core;

namespace Nethermind.Consensus.Qbft.Validators;

/// <summary>Source of the validator set, either tallied from block headers or read from the validator contract.</summary>
public interface IValidatorProvider
{
    /// <summary>Validators that may propose and seal the block after the current head.</summary>
    IReadOnlyList<Address> GetValidatorsAtHead();

    /// <summary>Validators that may propose and seal the child of <paramref name="parentHeader"/>.</summary>
    IReadOnlyList<Address> GetValidatorsAfterBlock(BlockHeader parentHeader);

    /// <summary>Validators recorded for <paramref name="header"/> itself.</summary>
    IReadOnlyList<Address> GetValidatorsForBlock(BlockHeader header);

    /// <summary>The vote store at head, or null when validators come from a contract and voting is disabled.</summary>
    IVoteProvider? GetVoteProviderAtHead();

    IVoteProvider? GetVoteProviderAfterBlock(BlockHeader header) => GetVoteProviderAtHead();

    bool IsValidatorAtHead(Address address) => GetValidatorsAtHead().ContainsAddress(address);
}

/// <summary>Pending membership proposals of the local node and the vote to cast on the next proposed block.</summary>
public interface IVoteProvider
{
    ValidatorVote? GetVoteAfterBlock(BlockHeader header, Address localAddress);
    void AuthVote(Address address);
    void DropVote(Address address);
    void DiscardVote(Address address);
    IReadOnlyDictionary<Address, VoteType> GetProposals();
}

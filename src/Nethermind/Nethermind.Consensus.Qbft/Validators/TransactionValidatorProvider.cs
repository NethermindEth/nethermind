// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Consensus.Qbft.Config;
using Nethermind.Consensus.Qbft.Contracts;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.Qbft.Validators;

/// <summary>Validators read from the validator contract ("contract" selection mode); voting is not available.</summary>
/// <remarks>
/// The set for a block's child is read from the block's own state through the contract address the
/// fork schedule names for the child, so a transition can move the contract.
/// </remarks>
public sealed class TransactionValidatorProvider(IBlockTree blockTree, IValidatorContract validatorContract, QbftForksSchedule forksSchedule) : IValidatorProvider
{
    private const int CacheSize = 100;
    private readonly LruCache<Hash256AsKey, IReadOnlyList<Address>> _afterBlockCache = new(CacheSize, "qbft validators after block");
    private readonly LruCache<Hash256AsKey, IReadOnlyList<Address>> _forBlockCache = new(CacheSize, "qbft validators for block");

    public IReadOnlyList<Address> GetValidatorsAtHead() =>
        GetValidatorsAfterBlock(blockTree.Head?.Header ?? throw new InvalidOperationException("Block tree has no head."));

    public IReadOnlyList<Address> GetValidatorsAfterBlock(BlockHeader parentHeader)
    {
        Address contractAddress = ResolveContractAddress((long)parentHeader.Number + 1, parentHeader.Timestamp);
        return GetValidatorsFromContract(_afterBlockCache, parentHeader, contractAddress);
    }

    public IReadOnlyList<Address> GetValidatorsForBlock(BlockHeader header)
    {
        Address contractAddress = ResolveContractAddress((long)header.Number, header.Timestamp);
        return GetValidatorsFromContract(_forBlockCache, header, contractAddress);
    }

    public IVoteProvider? GetVoteProviderAtHead() => null;

    private IReadOnlyList<Address> GetValidatorsFromContract(LruCache<Hash256AsKey, IReadOnlyList<Address>> cache, BlockHeader header, Address contractAddress)
    {
        Hash256 hash = header.Hash ?? throw new ArgumentException("Header has no hash.", nameof(header));
        if (cache.TryGet(hash, out IReadOnlyList<Address>? cached))
        {
            return cached;
        }

        Address[] validators = validatorContract.GetValidators(header, contractAddress);
        Array.Sort(validators);
        cache.Set(hash, validators);
        return validators;
    }

    private Address ResolveContractAddress(long blockNumber, ulong timestamp) =>
        forksSchedule.GetFork(blockNumber, timestamp).ValidatorContractAddress
        ?? throw new InvalidOperationException("Error resolving smart contract address unable to make validator contract call");
}

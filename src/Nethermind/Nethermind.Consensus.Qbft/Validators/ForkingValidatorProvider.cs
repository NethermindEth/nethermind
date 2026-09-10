// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Consensus.Qbft.Config;
using Nethermind.Core;

namespace Nethermind.Consensus.Qbft.Validators;

/// <summary>Delegates to the block-header or contract provider according to the selection mode in force at each height.</summary>
public sealed class ForkingValidatorProvider(
    IBlockTree blockTree,
    QbftForksSchedule forksSchedule,
    IValidatorProvider blockValidatorProvider,
    IValidatorProvider transactionValidatorProvider) : IValidatorProvider
{
    public IReadOnlyList<Address> GetValidatorsAtHead() => GetValidatorsAfterBlock(Head);

    public IReadOnlyList<Address> GetValidatorsAfterBlock(BlockHeader parentHeader) =>
        Resolve((long)parentHeader.Number + 1, parentHeader.Timestamp).GetValidatorsAfterBlock(parentHeader);

    public IReadOnlyList<Address> GetValidatorsForBlock(BlockHeader header) =>
        Resolve((long)header.Number, header.Timestamp).GetValidatorsForBlock(header);

    public IVoteProvider? GetVoteProviderAtHead() => GetVoteProviderAfterBlock(Head);

    public IVoteProvider? GetVoteProviderAfterBlock(BlockHeader header) =>
        Resolve((long)header.Number + 1, header.Timestamp).GetVoteProviderAtHead();

    private BlockHeader Head => blockTree.Head?.Header ?? throw new InvalidOperationException("Block tree has no head.");

    private IValidatorProvider Resolve(long blockNumber, ulong timestamp) =>
        forksSchedule.GetFork(blockNumber, timestamp).IsValidatorContractMode ? transactionValidatorProvider : blockValidatorProvider;
}
